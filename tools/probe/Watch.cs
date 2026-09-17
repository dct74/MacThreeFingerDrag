using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TfdProbe;

/// <summary>
/// Long-running recorder. Samples both candidate backends at once:
///   A) raw HID (WM_INPUT, usage page 0x0D / usage 0x05)      -> works on Win10+
///   B) official touchpad pointer API (WM_POINTER + GetPointerTouchpadInfo) -> Win11 25H2+
/// and writes measurable facts to a log file.
/// </summary>
internal static class Watch
{
    private static StreamWriter _file;
    private static IntPtr _hidWnd, _ptrWnd;

    private static readonly Dictionary<uint, (ushort page, ushort usage, ushort lc)> DataMap = new();
    private static IntPtr _prep;
    private static int _slotCount;

    private static readonly Dictionary<int, uint> LastCid = new();
    private static readonly bool[] Seen = new bool[16];
    private static readonly List<double> AbsDelta = new();
    private static long _stationary, _moving;
    private static int _triSamplesLogged;
    private static int _lastDown;

    private sealed class Slot { public uint? tip, conf, cid, x, y, pres; }

    private static long _reports, _rawFail;
    private static double _lastTs;
    private static readonly List<double> Intervals = new();
    private static readonly Dictionary<uint, int> CCRate = new();
    private static readonly Dictionary<int, int> SlotDownCount = new();
    private static long _spikeIsolated, _spikeConsecutive;
    private static readonly double[] LastSlotX = new double[16];
    private static readonly double[] LastSlotY = new double[16];
    private static readonly bool[] PrevBig = new bool[16];
    private static long _ptrDown, _ptrUpdate, _ptrUp, _ptrMsgAny, _ptrOther;
    private static bool _ptrRegistered;
    private static int _hidSamplesLogged, _ptrSamplesLogged, _hexLogged;
    private static long _maxX = long.MinValue, _maxY = long.MinValue, _minX = long.MaxValue, _minY = long.MaxValue;
    private static long _f1, _f2, _f3, _f4, _f5;
    private static readonly List<double> ReportGaps = new();
    private static long _tipMismatch, _tipZeroWithCid, _tipOneWithXZero;

    public static int Run(int seconds, string outPath)
    {
        _file = new StreamWriter(outPath) { AutoFlush = true };
        Say("=== TfdProbe watch (v2: blocking GetMessage loop, usage-page-aware parse) ===");
        Say($"started {DateTime.Now:HH:mm:ss} duration={seconds}s pid={Environment.ProcessId} osBuild={Environment.OSVersion.Version}");

        OpenHidBackend();
        OpenPointerBackend();

        // progress + termination timers on the hidden window (message loop is blocking now)
        SetTimer(_hidWnd, TimerProgress, 10000, IntPtr.Zero);
        SetTimer(_hidWnd, TimerQuit, (uint)seconds * 1000, IntPtr.Zero);

        var sw = Stopwatch.StartNew();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER && msg.wParam.ToInt64() == TimerProgress)
            {
                Say($"[{(int)sw.Elapsed.TotalSeconds}s] reports={_reports} hidFail={_rawFail} ptrMsgs={_ptrMsgAny} ptrThread={_ptrThreadMsgs} " +
                    $"ptr(D/U/Up/other)={_ptrDown}/{_ptrUpdate}/{_ptrUp}/{_ptrOther} fg=0x{GetForegroundWindow().ToInt64():X}");
                continue;
            }
            if (msg.message == WM_TIMER && msg.wParam.ToInt64() == TimerQuit) break;
            if (msg.message == WM_INPUT) HandleRawInput(msg.lParam);
            else if (msg.message == WM_INPUT_DEVICE_CHANGE) Say($"[{sw.Elapsed.TotalSeconds:F1}s] WM_INPUT_DEVICE_CHANGE wParam={msg.wParam} lParam=0x{msg.lParam.ToInt64():X}");
            else if (msg.message >= 0x0246 && msg.message <= 0x024F) HandlePointerMsg(msg, false);
            else if (msg.message == WM_POINTER_THREAD_MARK) _ptrThreadMsgs++;
        }

        Summarize();
        Say("=== end ===");
        return 0;
    }

    private const uint WM_TIMER = 0x0113;
    private const uint WM_POINTER_THREAD_MARK = 0x8000 + 77;
    private const int PointerThreadMsgBase = 0x0246;
    private static long _ptrThreadMsgs;

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, uint id, uint ms, IntPtr proc);
    private const uint TimerProgress = 1;
    private const uint TimerQuit = 2;

    private static void Say(string s)
    {
        Console.WriteLine(s);
        _file?.WriteLine(s);
    }

    // ------------------------------------------------------------------ raw HID backend

    private static void OpenHidBackend()
    {
        _hidWnd = CreateWindowEx(0, "STATIC", "tfd-hid", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var rid = new RAWINPUTDEVICE { usUsagePage = 0x000D, usUsage = 0x0005, dwFlags = 0x00000100 | 0x00002000, hwndTarget = _hidWnd };
        var ok = RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Say($"[hid] window=0x{_hidWnd.ToInt64():X} RegisterRawInputDevices(INPUTSINK|DEVNOTIFY)={ok} err={Marshal.GetLastWin32Error()}");

        uint count = 1;
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        var list = new RAWINPUTDEVICELIST[count];
        GetRawInputDeviceList(list, ref count, size);

        foreach (var d in list)
        {
            if (d.dwType != 2) continue;
            uint psz = 0;
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref psz) != 0 || psz == 0) continue;
            var p = Marshal.AllocHGlobal((int)psz);
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_PREPARSEDDATA, p, ref psz) != psz) { Marshal.FreeHGlobal(p); continue; }
            if (HidP_GetCaps(p, out var caps) != HIDP_STATUS_SUCCESS || caps.UsagePage != 0x000D || caps.Usage != 0x0005)
            {
                Marshal.FreeHGlobal(p);
                continue;
            }
            ushort vl = caps.NumberInputValueCaps;
            var vc = new HIDP_VALUE_CAPS[vl];
            if (HidP_GetValueCaps(0, vc, ref vl, p) == HIDP_STATUS_SUCCESS)
                foreach (var v in vc) DataMap[v.DataIndex] = (v.UsagePage, v.Usage, v.LinkCollection);

            ushort bl = caps.NumberInputButtonCaps;
            var bstride = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
            var bbuf = Marshal.AllocHGlobal(bstride * (bl + 4));
            if (HidP_GetButtonCaps(0, bbuf, ref bl, p) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < bl; i++)
                {
                    var bc = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(IntPtr.Add(bbuf, i * bstride));
                    if (bc.ReportID != 5) continue;
                    DataMap[bc.DataIndex] = (bc.UsagePage, bc.Usage, bc.LinkCollection);
                }
            Marshal.FreeHGlobal(bbuf);

            _prep = p;
            _slotCount = DataMap.Values.Where(m => m.lc != 0).Select(m => (int)m.lc).Distinct().Count();
            Say($"[hid] cached preparsed for hwnd=0x{d.hDevice.ToInt64():X} dataIndices={DataMap.Count} contactSlots={_slotCount} reportLen={caps.InputReportByteLength} reportId=5");
        }
    }

    private static void HandleRawInput(IntPtr lParam)
    {
        uint rs = 0;
        var hsz = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref rs, hsz) != 0) { _rawFail++; return; }
        var buf = Marshal.AllocHGlobal((int)rs);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref rs, hsz) != rs) { _rawFail++; return; }
            var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (hdr.dwType != 2) { _rawFail++; return; }
            int hdrSize = Marshal.SizeOf<RAWINPUTHEADER>();
            uint sizeHid = (uint)Marshal.ReadInt32(buf, hdrSize);
            uint nCount = (uint)Marshal.ReadInt32(buf, hdrSize + 4);
            int dataOff = hdrSize + 8;

            double ts = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (_lastTs > 0)
            {
                var iv = ts - _lastTs;
                Intervals.Add(iv);
                if (iv > 30) ReportGaps.Add(iv);
            }
            _lastTs = ts;
            _reports++;

            if (_hexLogged < 4)
            {
                var sb = new StringBuilder();
                for (uint c = 0; c < nCount && c < 2; c++)
                {
                    for (uint i = 0; i < sizeHid; i++) sb.Append(Marshal.ReadByte(buf, dataOff + (int)(c * sizeHid + i)).ToString("X2"));
                    sb.Append(' ');
                }
                Say($"[hex#{_hexLogged}] sizeHid={sizeHid} dwCount={nCount} bytes={sb}");
                _hexLogged++;
            }

            if (_prep == IntPtr.Zero) return;
            var data = new HIDP_DATA[512];
            uint dlen = 512;
            if (HidP_GetData(0, data, ref dlen, _prep, IntPtr.Add(buf, dataOff), sizeHid * nCount) != HIDP_STATUS_SUCCESS) { _rawFail++; return; }

            var slots = new Dictionary<int, Slot>();
            uint cc = 0;
            for (uint i = 0; i < dlen; i++)
            {
                if (!DataMap.TryGetValue(data[i].DataIndex, out var m)) continue;
                if (m.lc == 0)
                {
                    if (m.page == 0x0D && m.usage == 0x54) cc = data[i].RawValue;
                    continue;
                }
                if (!slots.TryGetValue(m.lc, out var s)) s = new Slot();
                if (m.page == 0x0D && m.usage == 0x42) s.tip = data[i].RawValue;      // Tip Switch (button)
                else if (m.page == 0x0D && m.usage == 0x47) s.conf = data[i].RawValue; // Confidence (button)
                else if (m.page == 0x0D && m.usage == 0x51) s.cid = data[i].RawValue;  // Contact Id
                else if (m.page == 0x01 && m.usage == 0x30) s.x = data[i].RawValue;    // X  (NOTE: Pressure is 0x0D/0x30!)
                else if (m.page == 0x01 && m.usage == 0x31) s.y = data[i].RawValue;    // Y
                else if (m.page == 0x0D && m.usage == 0x30) s.pres = data[i].RawValue; // Pressure
                slots[m.lc] = s;
            }

            int down = slots.Count(k => k.Value.tip == 1);

            // report-rate gaps: distinguishes "fingers lifted" from "device silent while resting"
            if (Intervals.Count > 0 && Intervals[^1] > 30)
            {
                var act = string.Join(" ", slots.Where(k => k.Value.tip == 1).OrderBy(k => k.Key)
                    .Select(k => $"LC{k.Key}(cid={k.Value.cid} {k.Value.x},{k.Value.y})"));
                Say($"[gap] {Intervals[^1]:F1}ms  prevDown={_lastDown} curDown={down}  cur=[{act}]");
            }

            // ContactId stability (is cid per-contact or per-slot?)
            foreach (var kv in slots)
            {
                if (LastCid.TryGetValue(kv.Key, out var old) && kv.Value.tip == 1 && old != kv.Value.cid)
                    Say($"[cid] LC{kv.Key} {old} -> {kv.Value.cid}  (down={down})");
                if (kv.Value.tip == 1 && kv.Value.cid.HasValue) LastCid[kv.Key] = kv.Value.cid.Value;
            }

            foreach (var kv in slots)
            {
                if (kv.Value.tip == 0 && kv.Value.cid > 0 && kv.Value.x > 0) _tipZeroWithCid++;
                if (kv.Value.tip == 1 && kv.Value.x == 0 && kv.Value.y == 0) _tipOneWithXZero++;
            }
            if (cc != down) _tipMismatch++;

            CCRate[cc] = CCRate.GetValueOrDefault(cc) + 1;
            if (down == 1) _f1++; else if (down == 2) _f2++; else if (down == 3) _f3++; else if (down == 4) _f4++; else if (down >= 5) _f5++;

            if (_hidSamplesLogged < 20 && down > 0)
            {
                var parts = string.Join(" ", slots.OrderBy(k => k.Key).Where(k => k.Value.tip == 1)
                    .Select(k => $"LC{k.Key}[tip={k.Value.tip} cid={k.Value.cid} x={k.Value.x} y={k.Value.y} p={k.Value.pres}]"));
                Say($"[hid#{_hidSamplesLogged}] dT={(Intervals.Count > 0 ? Intervals[^1].ToString("F2") : "?")}ms cC={cc} down={down} {parts}");
                _hidSamplesLogged++;
            }

            if (_triSamplesLogged < 40 && down >= 3)
            {
                var parts = string.Join(" ", slots.OrderBy(k => k.Key)
                    .Select(k => $"LC{k.Key}[tip={k.Value.tip} conf={k.Value.conf} cid={k.Value.cid} x={k.Value.x} y={k.Value.y} p={k.Value.pres}]"));
                Say($"[3F#{_triSamplesLogged}] dT={(Intervals.Count > 0 ? Intervals[^1].ToString("F2") : "?")}ms cC={cc} down={down} {parts}");
                _triSamplesLogged++;
            }

            foreach (var kv in slots.Where(k => k.Value.tip == 1))
            {
                int lc = kv.Key;
                double x = kv.Value.x ?? 0, y = kv.Value.y ?? 0;
                double px = LastSlotX[lc], py = LastSlotY[lc];
                if (Seen[lc])
                {
                    double d = Math.Sqrt(Math.Pow(x - px, 2) + Math.Pow(y - py, 2));
                    AbsDelta.Add(d);
                    if (d > 50) { if (PrevBig[lc]) _spikeConsecutive++; else _spikeIsolated++; }
                    PrevBig[lc] = d > 50;
                    if (d == 0) _stationary++; else _moving++;
                }
                Seen[lc] = true;
                if ((int)x > _maxX) _maxX = (int)x;
                if ((int)y > _maxY) _maxY = (int)y;
                if ((int)x < _minX) _minX = (int)x;
                if ((int)y < _minY) _minY = (int)y;
                LastSlotX[lc] = x; LastSlotY[lc] = y;
                SlotDownCount[lc] = SlotDownCount.GetValueOrDefault(lc) + 1;
            }
            _lastDown = down;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ------------------------------------------------------------------ WM_POINTER backend

    private static void OpenPointerBackend()
    {
        const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        _ptrWnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "tfd-ptr", WS_POPUP,
            -4000, -4000, 200, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ptrWnd == IntPtr.Zero) { Say($"[ptr] CreateWindowEx failed err={Marshal.GetLastWin32Error()}"); return; }
        ShowWindow(_ptrWnd, 4 /*SW_SHOWNOACTIVATE*/);

        var p = GetProcAddress(GetModuleHandle("user32.dll"), "RegisterTouchpadCapableWindow");
        Say($"[ptr] window=0x{_ptrWnd.ToInt64():X} RegisterTouchpadCapableWindow export={(p == IntPtr.Zero ? "MISSING" : "present")}");
        if (p == IntPtr.Zero) return;
        var fn = Marshal.GetDelegateForFunctionPointer<RegTouchpadCapable>(p);
        _ptrRegistered = fn(_ptrWnd, true);
        Say($"[ptr] RegisterTouchpadCapableWindow(hwnd,TRUE)={_ptrRegistered} err={Marshal.GetLastWin32Error()}");
        Say($"[ptr] foreground=0x{GetForegroundWindow().ToInt64():X} ours=0x{_ptrWnd.ToInt64():X} -> we are NOT the foreground window");

        // also try the thread-registration variant (no window needed) on this thread
        var pt = GetProcAddress(GetModuleHandle("user32.dll"), "RegisterTouchpadCapableThread");
        Say($"[ptr] RegisterTouchpadCapableThread export={(pt == IntPtr.Zero ? "MISSING" : "present")}");
        if (pt != IntPtr.Zero)
        {
            var fn2 = Marshal.GetDelegateForFunctionPointer<RegTouchpadCapable>(pt);
            var ok2 = fn2(IntPtr.Zero, true);
            Say($"[ptr] RegisterTouchpadCapableThread(TRUE)={ok2} err={Marshal.GetLastWin32Error()}");
        }
    }

    private delegate bool RegTouchpadCapable(IntPtr hwnd, bool enable);
    private delegate bool GetPtrTouchpadInfo(uint pointerId, ref POINTER_TOUCHPAD_INFO info);

    private static void HandlePointerMsg(MSG msg, bool threadVariant)
    {
        if (threadVariant) { _ptrThreadMsgs++; }
        else _ptrMsgAny++;
        var id = (uint)(msg.wParam.ToInt64() & 0xFFFF);
        switch (msg.message)
        {
            case 0x0246: _ptrDown++; break;
            case 0x0247: _ptrUpdate++; break;
            case 0x0248: _ptrUp++; break;
            default: _ptrOther++; return;
        }

        var ptr = GetProcAddress(GetModuleHandle("user32.dll"), "GetPointerTouchpadInfo");
        if (ptr == IntPtr.Zero || _ptrSamplesLogged >= 25) return;
        var fn = Marshal.GetDelegateForFunctionPointer<GetPtrTouchpadInfo>(ptr);
        var ti = new POINTER_TOUCHPAD_INFO();
        var ok = fn(id, ref ti);
        Say($"[ptr#{_ptrSamplesLogged}] msg=0x{msg.message:X} id={id} ok={ok} err={Marshal.GetLastWin32Error()} " +
            $"type={ti.pointerInfo.pointerType} flags=0x{ti.pointerInfo.pointerFlags:X} hwndTarget=0x{ti.pointerInfo.hwndTarget.ToInt64():X} " +
            $"hid=({ti.pointerInfo.ptHimetricLocation.x},{ti.pointerInfo.ptHimetricLocation.y}) px=({ti.pointerInfo.ptPixelLocation.x},{ti.pointerInfo.ptPixelLocation.y}) " +
            $"touchFlags=0x{ti.touchFlags:X} mask=0x{ti.touchMask:X} contact={ti.rcContact.left},{ti.rcContact.top},{ti.rcContact.right - ti.rcContact.left}x{ti.rcContact.bottom - ti.rcContact.top} " +
            $"pressure={ti.pressure} orientation={ti.orientation} time={ti.pointerInfo.dwTime} frame={ti.pointerInfo.frameId}");
        _ptrSamplesLogged++;
    }

    // ------------------------------------------------------------------ summary

    private static void Summarize()
    {
        Say("");
        Say("=== SUMMARY ===");
        Say($"raw HID: reports={_reports} parseFail={_rawFail} contactSlots={_slotCount}");
        if (Intervals.Count > 0)
        {
            var s = Intervals.OrderBy(x => x).ToList();
            var noGap = Intervals.Where(x => x < 100).ToList();
            Say($"  interval ms: mean={Intervals.Average():F2} p50={s[s.Count / 2]:F2} p95={s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))]:F2} max={s[^1]:F2} gaps>30ms={ReportGaps.Count}");
            if (noGap.Count > 0)
                Say($"  excluding gaps>100ms: n={noGap.Count} mean={noGap.Average():F2}ms -> ~{1000.0 / noGap.Average():F0} Hz");
        }
        Say($"  frames by TipSwitch==1 count: 1={_f1} 2={_f2} 3={_f3} 4={_f4} 5+={_f5}");
        Say($"  ContactCount histogram: {string.Join(", ", CCRate.OrderBy(k => k.Key).Select(k => $"{k.Key}->{k.Value}"))}");
        Say($"  mismatch(ContactCount != tipSwitchCount) frames: {_tipMismatch}; tip0-with-cid>0: {_tipZeroWithCid}; tip1-but-xy0: {_tipOneWithXZero}");
        Say($"  observed X {(_minX == long.MaxValue ? 0 : _minX)}..{_maxX}  Y {(_minY == long.MaxValue ? 0 : _minY)}..{_maxY}   (descriptor logical max X=12992 Y=7855)");
        if (AbsDelta.Count > 0)
        {
            var d = AbsDelta.OrderBy(v => v).ToList();
            Say($"  per-frame slot displacement (units): n={AbsDelta.Count} p50={d[d.Count / 2]:F1} p90={d[Math.Min(d.Count - 1, (int)(d.Count * 0.9))]:F1} p99={d[Math.Min(d.Count - 1, (int)(d.Count * 0.99))]:F1} max={d[^1]:F1}");
            Say($"  stationary frames (same coords): {_stationary}  moving frames: {_moving}");
        }
        Say($"  jump>50units: isolated={_spikeIsolated} consecutive={_spikeConsecutive}");
        Say($"  per-slot down frames: {string.Join(", ", SlotDownCount.OrderBy(k => k.Key).Select(k => $"LC{k.Key}->{k.Value}"))}");
        Say($"WM_POINTER(background window): registered={_ptrRegistered} msgs={_ptrMsgAny} DOWN={_ptrDown} UPDATE={_ptrUpdate} UP={_ptrUp} other={_ptrOther}");
        Say($"WM_POINTER(thread registration): msgs={_ptrThreadMsgs} (0 => background tray tool cannot use the official touchpad pointer API)");
    }

    // ------------------------------------------------------------------ interop

    private const uint WM_INPUT = 0x00FF;
    private const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
    private const uint PM_REMOVE = 1;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct HIDP_DATA { public ushort DataIndex; public ushort Reserved; public uint RawValue; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage; public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
        public byte Reserved;
        public ushort BitSize, ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public ushort[] Reserved2;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        public ushort Usage => UsageMin;
        public ushort DataIndex => DataIndexMin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage; public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute;
        public ushort ReportCount, Reserved2;
        public uint R0, R1, R2, R3, R4, R5, R6, R7, R8;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        public ushort Usage => UsageMin;
        public ushort DataIndex => DataIndexMin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int inputData, dwKeyStates; public ulong PerformanceCount; public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCHPAD_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] l, ref uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr d, ref uint size);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr d, ref uint size, uint hsz);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("hid.dll")] private static extern uint HidP_GetCaps(IntPtr prep, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern uint HidP_GetValueCaps(ushort t, [Out] HIDP_VALUE_CAPS[] c, ref ushort len, IntPtr prep);
    [DllImport("hid.dll")] private static extern uint HidP_GetButtonCaps(ushort t, IntPtr c, ref ushort len, IntPtr prep);
    [DllImport("hid.dll")] private static extern uint HidP_GetData(ushort t, [Out] HIDP_DATA[] d, ref uint len, IntPtr prep, IntPtr report, uint reportLen);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr h, string n);
}
