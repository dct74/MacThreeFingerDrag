// TfdProbe - dumps the macOS-style-three-finger-drag relevant HID facts for the local precision touchpad.
// Usage: tfdprobe caps           -> dump HID caps/value caps/button caps for every PTP device
//        tfdprobe capture <sec>  -> dump live report rates + parsed contacts for <sec> seconds
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TfdProbe;

internal static class Program
{
    private const string WndClass = "TfdProbeWndClass";

    private static IntPtr _hwndMessageOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);

        var mode = args.Length > 0 ? args[0] : "caps";

        Console.WriteLine("=== Raw Input HID device list ===");
        var ptp = DumpDeviceList();

        if (ptp.Count == 0)
        {
            Console.WriteLine("!! No usage page 0x0D / usage 0x05 (Touch Pad) raw-input device found.");
            return 1;
        }

        if (mode == "caps" || mode == "capture")
        {
            Console.WriteLine();
            foreach (var d in ptp)
            {
                Console.WriteLine($"=== HID descriptor dump: {d.Path} ===");
                DumpCaps(d.HDevice);
                Console.WriteLine();
            }
        }

        if (mode == "capture")
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
            Capture(seconds, ptp);
        }
        else if (mode == "accel")
        {
            return AccelTest.Run();
        }
        else if (mode == "watch")
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 300;
            var outPath = args.Length > 2 ? args[2] : "watch.log";
            return Watch.Run(seconds, outPath);
        }

        return 0;
    }

    // ---------------------------------------------------------------- device list

    private sealed class HidDev
    {
        public IntPtr HDevice;
        public string Path;
        public ushort UsagePage, Usage;
        public uint VendorId, ProductId, Version;
    }

    private static List<HidDev> DumpDeviceList()
    {
        var result = new List<HidDev>();

        uint count = 0;
        var listSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        var rc = GetRawInputDeviceList(null, ref count, listSize);
        Console.WriteLine($"  GetRawInputDeviceList(NULL) rc={rc} count={count} err={Marshal.GetLastWin32Error()} structSize={listSize}");
        if (count == 0) return result;

        var devices = new RAWINPUTDEVICELIST[count];
        var rc2 = GetRawInputDeviceList(devices, ref count, listSize);
        Console.WriteLine($"  GetRawInputDeviceList(buf) rc={rc2} count={count} err={Marshal.GetLastWin32Error()}");
        if (rc2 != count) return result;

        foreach (var dev in devices.Where(x => x.dwType == RIM_TYPEHID))
        {
            ushort up, us;
            uint vid, pid, ver;
            if (!ReadHidInfo(dev.hDevice, out vid, out pid, out ver, out up, out us)) continue;

            uint nameSize = 0;
            GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);
            var ptr = Marshal.AllocHGlobal((int)nameSize);
            GetRawInputDeviceInfo(dev.hDevice, RIDI_DEVICENAME, ptr, ref nameSize);
            var name = Marshal.PtrToStringAnsi(ptr);
            Marshal.FreeHGlobal(ptr);


            Console.WriteLine($"  hwnd=0x{dev.hDevice.ToInt64():X}  up=0x{up:X4} us=0x{us:X4}  VID={vid:X4} PID={pid:X4} rev={ver}");
            Console.WriteLine($"      {name}");

            if (up == 0x000D && us == 0x0005)
                result.Add(new HidDev { HDevice = dev.hDevice, Path = name, UsagePage = up, Usage = us, VendorId = vid, ProductId = pid, Version = ver });
        }
        return result;
    }

    private static bool ReadHidInfo(IntPtr hDevice, out uint vid, out uint pid, out uint ver, out ushort up, out ushort us)
    {
        vid = pid = ver = 0; up = us = 0;
        var buf = Marshal.AllocHGlobal(32);
        try
        {
            unsafe { *(uint*)buf = 32; }
            for (int i = 0; i < 32; i++) Marshal.WriteByte(buf, i, 0);
            unsafe { *(uint*)buf = 32; }

            uint size = 32;
            var rc = GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, buf, ref size);
            if (rc == unchecked((uint)-1)) return false;
            int off = sizeof(uint) * 2; // skip cbSize + dwType
            vid = (uint)Marshal.ReadInt32(buf, off);
            pid = (uint)Marshal.ReadInt32(buf, off + 4);
            ver = (uint)Marshal.ReadInt32(buf, off + 8);
            up = (ushort)Marshal.ReadInt16(buf, off + 12);
            us = (ushort)Marshal.ReadInt16(buf, off + 14);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void DumpCaps(IntPtr hDevice)
    {
        var prep = GetPreparsed(hDevice, out var prepSize);
        if (prep == IntPtr.Zero) { Console.WriteLine("  !! no preparsed data"); return; }

        try
        {
            var st = HidP_GetCaps(prep, out var caps);
            if (st != HIDP_STATUS_SUCCESS) { Console.WriteLine("  !! HidP_GetCaps failed 0x" + st.ToString("X8")); return; }

            Console.WriteLine($"  UsagePage=0x{caps.UsagePage:X4} Usage=0x{caps.Usage:X4} ({(caps.UsagePage == 0x0D && caps.Usage == 0x05 ? "Touch Pad" : "?")})");
            Console.WriteLine($"  InputReportByteLength={caps.InputReportByteLength}  NumberLinkCollectionNodes={caps.NumberLinkCollectionNodes}");
            Console.WriteLine($"  NumberInputValueCaps={caps.NumberInputValueCaps}  NumberInputButtonCaps={caps.NumberInputButtonCaps}  NumberInputDataIndices={caps.NumberInputDataIndices}");

            // ---- value caps
            ushort vlen = caps.NumberInputValueCaps;
            if (vlen > 0)
            {
                var vcaps = new HIDP_VALUE_CAPS[vlen];
                if (HidP_GetValueCaps(0, vcaps, ref vlen, prep) == HIDP_STATUS_SUCCESS)
                {
                    Console.WriteLine("  --- VALUE CAPS (per LinkCollection = per contact slot) ---");
                    foreach (var v in vcaps.OrderBy(x => x.LinkCollection).ThenBy(x => x.DataIndex))
                    {
                        Console.WriteLine(
                            $"    LC={v.LinkCollection,-3} LinkUsage=0x{v.LinkUsagePage:X2}/{v.LinkUsage:X2} " +
                            $"Usage=0x{v.UsagePage:X4}/{v.Usage:X4} {UsageName(v.UsagePage, v.Usage),-14} " +
                            $"BitSize={v.BitSize,-3} ReportCount={v.ReportCount,-3} Logical={v.LogicalMin}..{v.LogicalMax} " +
                            $"Physical={v.PhysicalMin}..{v.PhysicalMax} Units=0x{v.Units:X} UnitsExp=0x{v.UnitsExp:X8} " +
                            $"Abs={(v.IsAbsolute ? 1 : 0)} Null={(v.HasNull ? 1 : 0)} ReportID={v.ReportID} DataIndex={v.DataIndex} BitField=0x{v.BitField:X4}");
                    }
                }
            }

            // ---- button caps
            ushort blen = caps.NumberInputButtonCaps;
            if (blen > 0)
            {
                var bstride = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
                var nbuf = Marshal.AllocHGlobal(bstride * (blen + 4));
                try
                {
                    var bst = HidP_GetButtonCaps(0, nbuf, ref blen, prep);
                    Console.WriteLine($"  --- BUTTON CAPS (status=0x{bst:X8} len={blen} structSize={bstride}) ---");
                    if (bst == HIDP_STATUS_SUCCESS)
                    {
                        var bcaps = new HIDP_BUTTON_CAPS[blen];
                        for (int i = 0; i < blen; i++) bcaps[i] = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(IntPtr.Add(nbuf, i * bstride));

                        foreach (var b in bcaps.OrderBy(x => x.LinkCollection))
                        {
                            var use = b.IsRange ? $"0x{b.UsageMin:X4}..0x{b.UsageMax:X4}" : $"0x{b.Usage:X4}";
                            Console.WriteLine(
                                $"    LC={b.LinkCollection,-3} LinkUsage=0x{b.LinkUsagePage:X2}/{b.LinkUsage:X2} " +
                                $"UsagePage=0x{b.UsagePage:X4} Usage={use} {UsageName(b.UsagePage, b.Usage),-14} " +
                                $"ReportCount={b.ReportCount} " +
                                $"ReportID={b.ReportID} DataIndex={b.DataIndex} BitField=0x{b.BitField:X4}");
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(nbuf); }
            }
        }
        finally { Marshal.FreeHGlobal(prep); }
    }

    private static string UsageName(ushort page, ushort usage)
    {
        if (page == 0x000D && usage == 0x0001) return "TipSwitch";
        if (page == 0x01 && usage == 0x30) return "X";
        if (page == 0x01 && usage == 0x31) return "Y";
        if (page == 0x0D && usage == 0x22) return "Finger";
        if (page == 0x0D && usage == 0x42) return "TipSwitch";
        if (page == 0x0D && usage == 0x47) return "Confidence";
        if (page == 0x0D && usage == 0x48) return "ContactWidth";
        if (page == 0x0D && usage == 0x49) return "ContactHeight";
        if (page == 0x0D && usage == 0x51) return "ContactId";
        if (page == 0x0D && usage == 0x52) return "DeviceMode";
        if (page == 0x0D && usage == 0x54) return "ContactCount";
        if (page == 0x0D && usage == 0x55) return "ContactCountMax";
        if (page == 0x0D && usage == 0x56) return "ScanTime";
        if (page == 0x0D && usage == 0x30) return "Pressure";
        if (page == 0x0D && usage == 0x3F) return "Altitude";
        if (page == 0x0D && usage == 0x00) return "(latency)";
        return "";
    }

    private static IntPtr GetPreparsed(IntPtr hDevice, out uint size)
    {
        size = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0) return IntPtr.Zero;
        var ptr = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, ptr, ref size) != size) { Marshal.FreeHGlobal(ptr); return IntPtr.Zero; }
        return ptr;
    }

    // ---------------------------------------------------------------- capture

    private static void Capture(int seconds, List<HidDev> ptp)
    {
        var prepCache = new Dictionary<IntPtr, IntPtr>();
        foreach (var d in ptp) prepCache[d.HDevice] = GetPreparsed(d.HDevice, out _);

        // Build DataIndex -> (usage, linkCollection) map for the fastest possible parse path
        var dataIndexMap = new Dictionary<uint, (ushort usagePage, ushort usage, ushort lc)>();
        foreach (var d in ptp)
        {
            if (prepCache[d.HDevice] == IntPtr.Zero) continue;
            if (HidP_GetCaps(prepCache[d.HDevice], out var caps) != HIDP_STATUS_SUCCESS) continue;
            ushort vlen = caps.NumberInputValueCaps;
            if (vlen == 0) continue;
            var vcaps = new HIDP_VALUE_CAPS[vlen];
            if (HidP_GetValueCaps(0, vcaps, ref vlen, prepCache[d.HDevice]) != HIDP_STATUS_SUCCESS) continue;
            foreach (var v in vcaps) dataIndexMap[v.DataIndex] = (v.UsagePage, v.Usage, v.LinkCollection);
        }

        _hwndMessageOnly = CreateMessageWindow();

        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x000D,
            usUsage = 0x0005,
            dwFlags = 0x00000100, // RIDEV_INPUTSINK
            hwndTarget = _hwndMessageOnly
        };
        if (!RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Console.WriteLine("!! RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
            return;
        }

        Console.WriteLine($"=== Capture for {seconds}s (touch the trackpad now: 1 finger, then 3 fingers and drag) ===");

        var sw = Stopwatch.StartNew();
        var intervals = new List<double>();
        long lastTicks = -1;
        int reports = 0, parsedOk = 0, parseFail = 0;
        var histByContactCount = new Dictionary<uint, int>();
        var parseCostPerUsage = new List<double>();
        var parseCostHidGetData = new List<double>();
        var samples = new List<string>();

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (!PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                System.Threading.Thread.Sleep(1);
                continue;
            }
            if (msg.message != WM_INPUT) continue;

            long now = Stopwatch.GetTimestamp();
            if (lastTicks >= 0) intervals.Add((now - lastTicks) * 1000.0 / Stopwatch.Frequency);
            lastTicks = now;
            reports++;

            // --- raw bytes + HIDP_GetData path (fastest)
            uint rs = 0;
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            if (GetRawInputData(msg.lParam, RID_INPUT, IntPtr.Zero, ref rs, headerSize) != 0) { parseFail++; continue; }
            var buf = Marshal.AllocHGlobal((int)rs);
            try
            {
                if (GetRawInputData(msg.lParam, RID_INPUT, buf, ref rs, headerSize) != rs) { parseFail++; continue; }
                var hdr = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                uint sizeHid = (uint)Marshal.ReadInt32(buf, Marshal.SizeOf<RAWINPUTHEADER>());
                uint nCount = (uint)Marshal.ReadInt32(buf, Marshal.SizeOf<RAWINPUTHEADER>() + 4);
                int dataOff = (int)Marshal.SizeOf<RAWINPUTHEADER>() + 8;

                var sb = new StringBuilder();
                for (uint c = 0; c < nCount; c++)
                {
                    for (uint i = 0; i < sizeHid; i++)
                        sb.Append(Marshal.ReadByte(buf, dataOff + (int)(c * sizeHid + i)).ToString("X2"));
                    sb.Append(' ');
                }

                if (reports <= 40 || reports % 25 == 0)
                    samples.Add($"    #{reports} sizeHid={sizeHid} dwCount={nCount} raw={sb}");

                // HidP_GetData timing
                if (prepCache.TryGetValue(hdr.hDevice, out var prep) && prep != IntPtr.Zero)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var data = new HIDP_DATA[256];
                    uint dlen = 256;
                    var st = HidP_GetData(0, data, ref dlen, prep, IntPtr.Add(buf, dataOff), sizeHid * nCount);
                    var t1 = Stopwatch.GetTimestamp();
                    parseCostHidGetData.Add((t1 - t0) * 1000000.0 / Stopwatch.Frequency);

                    if (st == HIDP_STATUS_SUCCESS)
                    {
                        parsedOk++;
                        if (reports <= 40 || reports % 25 == 0)
                        {
                            var parts = new List<string>();
                            uint cc = 0;
                            for (uint i = 0; i < dlen; i++)
                            {
                                if (!dataIndexMap.TryGetValue(data[i].DataIndex, out var m)) continue;
                                if (m.usagePage == 0x0D && m.usage == 0x54) cc = data[i].RawValue;
                                parts.Add($"LC{m.lc}/0x{m.usagePage:X}/{m.usage:X}={data[i].RawValue}");
                            }
                            histByContactCount[cc] = histByContactCount.GetValueOrDefault(cc) + 1;
                            samples.Add($"       GetData({dlen}) cC={cc}: {string.Join(" ", parts)}");
                        }
                    }
                    else parseFail++;

                    // per-usage slow path timing (what ThreeFingerDragOnWindows does)
                    var t2 = Stopwatch.GetTimestamp();
                    if (HidP_GetCaps(prep, out var caps) == HIDP_STATUS_SUCCESS)
                    {
                        ushort vlen = caps.NumberInputValueCaps;
                        var vcaps = new HIDP_VALUE_CAPS[vlen];
                        if (HidP_GetValueCaps(0, vcaps, ref vlen, prep) == HIDP_STATUS_SUCCESS)
                        {
                            for (uint c = 0; c < nCount; c++)
                            {
                                foreach (var v in vcaps)
                                {
                                    HidP_GetUsageValue(0, v.UsagePage, v.LinkCollection, v.Usage, out _,
                                        prep, IntPtr.Add(buf, dataOff + (int)(c * sizeHid)), sizeHid);
                                }
                            }
                        }
                    }
                    // also time the per-call GetRawInputDeviceInfo(PREPARSEDDATA) the original does per message
                    var t3 = Stopwatch.GetTimestamp();
                    uint psz = 0;
                    GetRawInputDeviceInfo(hdr.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref psz);
                    var tmp = Marshal.AllocHGlobal((int)psz);
                    GetRawInputDeviceInfo(hdr.hDevice, RIDI_PREPARSEDDATA, tmp, ref psz);
                    Marshal.FreeHGlobal(tmp);
                    var t4 = Stopwatch.GetTimestamp();
                    parseCostPerUsage.Add((t4 - t2) * 1000000.0 / Stopwatch.Frequency);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        foreach (var s in samples.Take(400)) Console.WriteLine(s);

        Console.WriteLine();
        Console.WriteLine("=== capture stats ===");
        Console.WriteLine($"  reports={reports} parsedOk={parsedOk} parseFail={parseFail} duration={sw.Elapsed.TotalSeconds:F2}s");
        if (intervals.Count > 0)
        {
            var sorted = intervals.OrderBy(x => x).ToList();
            Console.WriteLine($"  report interval ms: n={intervals.Count} mean={intervals.Average():F2} p50={sorted[sorted.Count / 2]:F2} p95={sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))]:F2} max={sorted[^1]:F2}");
            Console.WriteLine($"  -> effective report rate ~ {1000.0 / intervals.Average():F0} Hz");
        }
        Console.WriteLine($"  contactCount histogram: {string.Join(", ", histByContactCount.Select(kv => $"{kv.Key}->{kv.Value}"))}");
        if (parseCostHidGetData.Count > 0)
            Console.WriteLine($"  parse cost per report: HidP_GetData={parseCostHidGetData.Average():F3}us  (per-usage loop + per-message preparsed re-fetch = {parseCostPerUsage.Average():F3}us)");
        Console.WriteLine($"  speedup of cached-DataIndex parse path: {(parseCostPerUsage.Count > 0 && parseCostHidGetData.Count > 0 ? (parseCostPerUsage.Average() / Math.Max(parseCostHidGetData.Average(), 0.001)) : 0):F1}x");

        foreach (var p in prepCache.Values) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        if (_hwndMessageOnly != IntPtr.Zero) DestroyWindow(_hwndMessageOnly);
    }

    private static IntPtr CreateMessageWindow()
    {
        // "STATIC" is a predefined class -> no RegisterClassEx / WndProc delegate lifetime issues.
        var hwnd = CreateWindowEx(0, "STATIC", "TfdProbe", 0x80000000u /*WS_POPUP*/, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) Console.WriteLine("!! CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        else Console.WriteLine($"  message-only window hwnd=0x{hwnd.ToInt64():X}");
        return hwnd;
    }

    // ---------------------------------------------------------------- interop

    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const uint PM_REMOVE = 1;
    private const uint HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO { public uint cbSize; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID { public uint dwVendorId, dwProductId, dwVersionNumber; public ushort usUsagePage, usUsage; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_DATA { public ushort DataIndex; public ushort Reserved; public uint RawValue; }

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
        public uint R0, R1, R2, R3, R4, R5, R6, R7, R8; // ULONG Reserved[9]
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        public ushort Usage => UsageMin;
        public ushort DataIndex => DataIndexMin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint num, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("hid.dll")] private static extern uint HidP_GetCaps(IntPtr prep, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern uint HidP_GetValueCaps(ushort type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr prep);
    [DllImport("hid.dll")] private static extern uint HidP_GetButtonCaps(ushort type, IntPtr caps, ref ushort len, IntPtr prep);
    [DllImport("hid.dll")] private static extern uint HidP_GetUsageValue(ushort type, ushort page, ushort lc, ushort usage, out uint val, IntPtr prep, IntPtr report, uint len);
    [DllImport("hid.dll")] private static extern uint HidP_GetData(ushort type, [Out] HIDP_DATA[] data, ref uint len, IntPtr prep, IntPtr report, uint reportLen);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
