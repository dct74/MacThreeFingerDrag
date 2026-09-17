using System.Runtime.InteropServices;
using System.Text;

namespace Mftd;

/// <summary>一个激活的触点（同一帧内）。</summary>
internal struct Contact
{
    public int Slot;        // HID LinkCollection 索引
    public uint Id;         // ContactId（同一次接触内稳定）
    public int X, Y;        // 逻辑坐标
}

/// <summary>帧的接收方（由手势引擎实现）。</summary>
internal interface IFrameSink
{
    /// <summary>一次 WM_INPUT 里可能有多份报告；reportCount &gt; 1 时会被逐帧回调，保证不丢轨迹。</summary>
    void OnTouchFrame(PtpTouchpad dev, Contact[] contacts, int count, double nowMs, int reportIndex, int reportCount);
}

/// <summary>一个 Precision Touchpad 设备的缓存信息。除热插拔外，解析期间不再分配/查询任何系统数据。</summary>
internal sealed unsafe class PtpTouchpad
{
    internal const byte KindIgnore = 0;
    internal const byte KindX = 1;
    internal const byte KindY = 2;
    internal const byte KindContactId = 3;
    internal const byte KindTipSwitch = 4;
    internal const byte KindConfidence = 5;
    internal const byte KindContactCount = 6;

    internal struct Entry
    {
        public byte Kind;
        public byte Slot;
    }

    public IntPtr RawHandle;
    public string Path = "";
    public ushort VendorId, ProductId;
    public IntPtr Preparsed;
    public int XMax = 1, YMax = 1;
    public bool HasTipSwitch;
    public bool HasContactCount;
    public int SlotCount;          // 触点槽位数（= 最大的 LinkCollection 索引）
    public readonly Entry[] Map = new Entry[256];

    public string ShortName
    {
        get
        {
            // \\?\HID#VID_05AC&PID_027B&MI_02&Col02#7&2d91e5bd&0&0001#{guid} -> HID\VID_05AC&PID_027B&MI_02&Col02
            var name = Path.Replace("\\\\?\\", "");
            var parts = name.Split('#');
            var head = parts.Length >= 2 ? parts[0] + "\\" + parts[1] : name;
            return $"{head} ({VendorId:X4}:{ProductId:X4})";
        }
    }

    public void Dispose()
    {
        if (Preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(Preparsed); Preparsed = IntPtr.Zero; }
    }
}

/// <summary>
/// 设备枚举 + Raw Input 解析。
/// 设计要点（见 DESIGN 文档 §4.2）：
///   · preparsed data / DataIndex 映射 / 逻辑量程 全部缓存，热插拔才重建；
///   · 每份报告只调用一次 HidP_GetData（替代上游每帧 22 次 HidP_GetUsageValue）；
///   · 缓冲复用，热路径零托管分配；
///   · 手指存在靠 TipSwitch(0x0D/0x42)，不依赖 ContactCount 的「触点列表重建」。
/// </summary>
internal sealed unsafe class TouchpadManager
{
    private const int MaxSlots = 16;
    private readonly List<PtpTouchpad> _devices = new();
    private IntPtr _hwnd;
    private IntPtr _rawBuf;
    private uint _rawBufCap;
    private IntPtr _dataBuf;
    private uint _dataCap;
    private readonly Contact[] _contacts = new Contact[MaxSlots];
    private readonly uint[] _slotTip = new uint[MaxSlots];
    private readonly uint[] _slotConf = new uint[MaxSlots];
    private readonly uint[] _slotId = new uint[MaxSlots];
    private readonly int[] _slotX = new int[MaxSlots];
    private readonly int[] _slotY = new int[MaxSlots];
    private readonly bool[] _slotSeen = new bool[MaxSlots];

    /// <summary>当前被驱动的设备（本机只有一块内建触控板；外接时取第一块）。</summary>
    public PtpTouchpad? Primary { get; private set; }
    public int DeviceCount => _devices.Count;
    public long FramesParsed;
    public long ParseFailures;

    public bool Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var rid = new Native.RAWINPUTDEVICE
        {
            usUsagePage = Native.USAGE_PAGE_DIGITIZER,
            usUsage = Native.USAGE_TOUCHPAD,
            // INPUTSINK: 窗口在后台也收得到；DEVNOTIFY: 顺便收设备热插拔通知
            dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY,
            hwndTarget = hwnd
        };
        bool ok = Native.RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        if (!ok) Log.Info($"RegisterRawInputDevices 失败 err={Marshal.GetLastWin32Error()}");
        _rawBufCap = 8192;
        _rawBuf = Marshal.AllocHGlobal((int)_rawBufCap);
        _dataCap = 512;
        _dataBuf = Marshal.AllocHGlobal((int)(_dataCap * sizeof(Native.HIDP_DATA)));
        return ok;
    }

    /// <summary>重新枚举设备（启动时 &amp; 热插拔时调用）。</summary>
    public void Refresh()
    {
        foreach (var d in _devices) d.Dispose();
        _devices.Clear();
        Primary = null;

        uint count = 1;
        uint structSize = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        if (Native.GetRawInputDeviceList(null, ref count, structSize) != 0 || count == 0) return;
        var list = new Native.RAWINPUTDEVICELIST[count];
        if (Native.GetRawInputDeviceList(list, ref count, structSize) != count) return;

        foreach (var item in list)
        {
            if (item.dwType != Native.RIM_TYPEHID) continue;
            var dev = Describe(item.hDevice);
            if (dev != null) _devices.Add(dev);
        }

        Primary = _devices.Count > 0 ? _devices[0] : null;
        if (Primary != null)
            Log.Info($"触控板: {Primary.ShortName} 触点槽={Primary.SlotCount} X量程=0..{Primary.XMax} Y量程=0..{Primary.YMax} TipSwitch={Primary.HasTipSwitch}");
        else
            Log.Info("未检测到 Precision Touchpad (usage page 0x0D / usage 0x05)");
    }

    private static PtpTouchpad? Describe(IntPtr hDevice)
    {
        // RID_DEVICE_INFO 最大 32 字节（union 里 KEYBOARD 占 24 字节）
        var info = Marshal.AllocHGlobal(32);
        var prepData = IntPtr.Zero;
        try
        {
            for (int i = 0; i < 32; i++) Marshal.WriteByte(info, i, 0);
            *(uint*)info = 32;
            uint size = 32;
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICEINFO, info, ref size) == unchecked((uint)-1)) return null;
            ushort usagePage = *(ushort*)((byte*)info + 20);
            ushort usage = *(ushort*)((byte*)info + 22);            if (usagePage != Native.USAGE_PAGE_DIGITIZER || usage != Native.USAGE_TOUCHPAD) return null;

            uint prepSize = 0;
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref prepSize) != 0 || prepSize == 0) return null;
            prepData = Marshal.AllocHGlobal((int)prepSize);
            if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_PREPARSEDDATA, prepData, ref prepSize) != prepSize)
            {
                Marshal.FreeHGlobal(prepData);
                return null;
            }

            if (Native.HidP_GetCaps(prepData, out var caps) != Native.HIDP_STATUS_SUCCESS)
            {
                Marshal.FreeHGlobal(prepData);
                return null;
            }
            if (caps.UsagePage != Native.USAGE_PAGE_DIGITIZER || caps.Usage != Native.USAGE_TOUCHPAD)
            {
                Marshal.FreeHGlobal(prepData);
                return null;
            }

            var dev = new PtpTouchpad
            {
                RawHandle = hDevice,
                Preparsed = prepData,
                VendorId = *(ushort*)((byte*)info + 8),
                ProductId = *(ushort*)((byte*)info + 12),
            };

            // 设备路径（用于日志 / 未来做 per-device 配置）
            uint nameSize = 0;
            Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);
            if (nameSize > 0)
            {
                var nameBuf = Marshal.AllocHGlobal((int)(nameSize * 2));
                if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, nameBuf, ref nameSize) != unchecked((uint)-1))
                    dev.Path = Marshal.PtrToStringUni(nameBuf) ?? "";
                Marshal.FreeHGlobal(nameBuf);
            }

            // 值：每个 LinkCollection > 0 的就是一个触点槽位（取 X/Y/ContactId）
            ushort vlen = caps.NumberInputValueCaps;
            if (vlen > 0)
            {
                var vcaps = new Native.HIDP_VALUE_CAPS[vlen];
                if (Native.HidP_GetValueCaps(0, vcaps, ref vlen, prepData) == Native.HIDP_STATUS_SUCCESS)
                {
                    foreach (var v in vcaps)
                    {
                        if (v.DataIndex >= dev.Map.Length) continue;
                        if (v.LinkCollection == 0)
                        {
                            if (v.UsagePage == Native.USAGE_PAGE_DIGITIZER && v.Usage == Native.USAGE_CONTACT_COUNT)
                            {
                                dev.HasContactCount = true;
                                dev.Map[v.DataIndex] = new PtpTouchpad.Entry { Kind = PtpTouchpad.KindContactCount };
                            }
                            continue;
                        }
                        byte kind = PtpTouchpad.KindIgnore;
                        if (v.UsagePage == Native.USAGE_PAGE_GENERIC && v.Usage == Native.USAGE_X)
                        {
                            kind = PtpTouchpad.KindX;
                            if (v.LogicalMax > dev.XMax) dev.XMax = v.LogicalMax;
                        }
                        else if (v.UsagePage == Native.USAGE_PAGE_GENERIC && v.Usage == Native.USAGE_Y)
                        {
                            kind = PtpTouchpad.KindY;
                            if (v.LogicalMax > dev.YMax) dev.YMax = v.LogicalMax;
                        }
                        else if (v.UsagePage == Native.USAGE_PAGE_DIGITIZER && v.Usage == Native.USAGE_CONTACT_ID)
                        {
                            kind = PtpTouchpad.KindContactId;
                        }
                        if (kind == PtpTouchpad.KindIgnore) continue;
                        dev.Map[v.DataIndex] = new PtpTouchpad.Entry { Kind = kind, Slot = (byte)v.LinkCollection };
                        if (v.LinkCollection > dev.SlotCount) dev.SlotCount = v.LinkCollection;
                    }
                }
            }
            if (dev.SlotCount > MaxSlots) dev.SlotCount = MaxSlots;


            // 按钮：TipSwitch / Confidence（本机 Apple 触控板确实提供，见 DESIGN §2.4）
            ushort blen = caps.NumberInputButtonCaps;
            if (blen > 0)
            {
                int stride = Marshal.SizeOf<Native.HIDP_BUTTON_CAPS>();
                var bbuf = Marshal.AllocHGlobal(stride * (blen + 4));
                try
                {
                    if (Native.HidP_GetButtonCaps(0, bbuf, ref blen, prepData) == Native.HIDP_STATUS_SUCCESS)
                    {
                        for (int i = 0; i < blen; i++)
                        {
                            var b = Marshal.PtrToStructure<Native.HIDP_BUTTON_CAPS>(IntPtr.Add(bbuf, i * stride));
                            if (b.LinkCollection == 0 || b.DataIndex >= dev.Map.Length) continue;
                            byte kind = b.UsagePage == Native.USAGE_PAGE_DIGITIZER
                                ? b.Usage switch
                                {
                                    Native.USAGE_TIP_SWITCH => PtpTouchpad.KindTipSwitch,
                                    Native.USAGE_CONFIDENCE => PtpTouchpad.KindConfidence,
                                    _ => PtpTouchpad.KindIgnore
                                }
                                : PtpTouchpad.KindIgnore;
                            if (kind == PtpTouchpad.KindIgnore) continue;
                            dev.Map[b.DataIndex] = new PtpTouchpad.Entry { Kind = kind, Slot = (byte)b.LinkCollection };
                            if (kind == PtpTouchpad.KindTipSwitch) dev.HasTipSwitch = true;
                            if (b.LinkCollection > dev.SlotCount) dev.SlotCount = b.LinkCollection;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(bbuf); }
            }

            if (dev.SlotCount == 0) { dev.Dispose(); return null; }
            return dev;
        }
        catch
        {
            if (prepData != IntPtr.Zero) Marshal.FreeHGlobal(prepData);
            return null;
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>处理 WM_INPUT。返回解析出的报告（帧）数。</summary>
    public int HandleRawInput(IntPtr lParam, IFrameSink sink, double nowMs)
    {
        var dev = Primary;
        if (dev == null) { ParseFailures++; return 0; }

        uint size = 0;
        const uint headerSize = 24; // sizeof(RAWINPUTHEADER) x64 (=dwType4+dwSize4+hDevice8+wParam8)
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0) { ParseFailures++; return 0; }
        if (size == 0 || size > 65536) { ParseFailures++; return 0; }
        if (size > _rawBufCap)
        {
            Marshal.FreeHGlobal(_rawBuf);
            _rawBufCap = size + 1024;
            _rawBuf = Marshal.AllocHGlobal((int)_rawBufCap);
        }
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, _rawBuf, ref size, headerSize) != size) { ParseFailures++; return 0; }

        byte* p = (byte*)_rawBuf;
        uint dwType = *(uint*)p;
        if (dwType != Native.RIM_TYPEHID) return 0;
        IntPtr hDevice = *(IntPtr*)(p + 8);
        // 已知怪癖：某些系统的内建触控板在 RAWINPUTHEADER.hDevice 里返回 NULL（见 DESIGN §1.3 #7）
        if (hDevice != IntPtr.Zero && dev.RawHandle != IntPtr.Zero && hDevice != dev.RawHandle)
        {
            var other = _devices.Find(d => d.RawHandle == hDevice);
            if (other == null) return 0;
            dev = other;
        }

        uint sizeHid = *(uint*)(p + 24);
        uint reportCount = *(uint*)(p + 28);
        if (sizeHid == 0 || reportCount == 0 || reportCount > 64) return 0;
        byte* reports = p + 32;

        int frames = 0;
        for (uint r = 0; r < reportCount; r++)
        {
            ParseOneReport(dev, reports + r * sizeHid, sizeHid, sink, nowMs, (int)r, (int)reportCount);
            frames++;
        }
        return frames;
    }

    private void ParseOneReport(PtpTouchpad dev, byte* report, uint reportLength, IFrameSink sink,
        double nowMs, int reportIndex, int reportCount)
    {
        uint dataLen = _dataCap;
        var status = Native.HidP_GetData(0, _dataBuf, ref dataLen, dev.Preparsed, (IntPtr)report, reportLength);
        if (status != Native.HIDP_STATUS_SUCCESS) { ParseFailures++; return; }
        FramesParsed++;

        int slots = dev.SlotCount;
        for (int i = 0; i <= slots && i < _slotTip.Length; i++)
        {
            _slotTip[i] = 0; _slotConf[i] = 0; _slotId[i] = 0; _slotX[i] = 0; _slotY[i] = 0; _slotSeen[i] = false;
        }

        var entries = (Native.HIDP_DATA*)_dataBuf;
        for (uint i = 0; i < dataLen; i++)
        {
            ref var e = ref entries[i];
            if (e.DataIndex >= dev.Map.Length) continue;
            var map = dev.Map[e.DataIndex];
            if (map.Kind == PtpTouchpad.KindIgnore) continue;
            if (map.Kind == PtpTouchpad.KindContactCount) continue; // 实测不可靠（0.5% 帧不一致），仅备用
            int s = map.Slot;
            if (s >= slots) continue;
            _slotSeen[s] = true;
            switch (map.Kind)
            {
                case PtpTouchpad.KindX: _slotX[s] = (int)e.RawValue; break;
                case PtpTouchpad.KindY: _slotY[s] = (int)e.RawValue; break;
                case PtpTouchpad.KindContactId: _slotId[s] = e.RawValue; break;
                case PtpTouchpad.KindTipSwitch: _slotTip[s] = e.RawValue; break;
                case PtpTouchpad.KindConfidence: _slotConf[s] = e.RawValue; break;
            }
        }

        int count = 0;
        for (int s = 1; s <= slots; s++)
        {
            // 手指在场上 = TipSwitch 置位；设备没有 TipSwitch 时退化为「有 Button 置位或 ContactId 非 0」
            bool active;
            if (dev.HasTipSwitch) active = _slotTip[s] == 1;
            else active = (_slotConf[s] == 1) || (_slotId[s] != 0 && (_slotX[s] != 0 || _slotY[s] != 0));
            if (!active) continue;
            _contacts[count].Slot = s;
            _contacts[count].Id = _slotId[s];
            _contacts[count].X = _slotX[s];
            _contacts[count].Y = _slotY[s];
            count++;
            if (count >= _contacts.Length) break;
        }

        sink.OnTouchFrame(dev, _contacts, count, nowMs, reportIndex, reportCount);
    }
}
