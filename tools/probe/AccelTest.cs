using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TfdProbe;

/// <summary>
/// Measures how the three candidate cursor-movement primitives behave:
///   1. SendInput relative MOUSEEVENTF_MOVE      (what ThreeFingerDragOnWindows uses)
///   2. SendInput absolute MOUSEEVENTF_ABSOLUTE  (virtual desktop normalised)
///   3. SetCursorPos                             (synchronous, no queueing)
/// Run once with the machine's current pointer settings, once with acceleration forced on.
/// </summary>
internal static class AccelTest
{
    public static int Run()
    {
        Console.WriteLine("=== current pointer settings ===");
        Console.WriteLine($"  SPI_GETMOUSE        : [0]={_mouse[0]} [1]={_mouse[1]} [2]={_mouse[2]}");
        Console.WriteLine($"  HKCU\\Control Panel\\Mouse: MouseSpeed={ReadReg("MouseSpeed")} Threshold1={ReadReg("MouseThreshold1")} Threshold2={ReadReg("MouseThreshold2")} Sensitivity={ReadReg("MouseSensitivity")}");
        Console.WriteLine($"  screen={GetSystemMetrics(SM_CXSCREEN)}x{GetSystemMetrics(SM_CYSCREEN)} virtual={GetSystemMetrics(SM_CXVIRTUALSCREEN)}x{GetSystemMetrics(SM_CYVIRTUALSCREEN)}");
        Console.WriteLine();

        var original = new POINT();
        GetCursorPos(out original);

        // anchor at centre of the primary screen so no test can hit an edge
        int cx = GetSystemMetrics(SM_CXSCREEN) / 2, cy = GetSystemMetrics(SM_CYSCREEN) / 2;

        RunSuite(cx, cy, "current settings");

        var saved = new int[] { _mouse[0], _mouse[1], _mouse[2] };
        var forced = new int[] { 0, 0, 3 }; // thr1=0 thr2=0 accel=3 -> most aggressive curve
        if (SystemParametersInfo(SPI_SETMOUSE, 0, forced, 0))
        {
            SystemParametersInfo(SPI_GETMOUSE, 0, _mouse, 0);
            Console.WriteLine($"=== pointer settings FORCED: [{_mouse[0]},{_mouse[1]},{_mouse[2]}] (accel=3, thresholds 0/0) ===");
            RunSuite(cx, cy, "accel forced on");
            SystemParametersInfo(SPI_SETMOUSE, 0, saved, 0);
            SystemParametersInfo(SPI_GETMOUSE, 0, _mouse, 0);
            Console.WriteLine($"=== pointer settings restored: [{_mouse[0]},{_mouse[1]},{_mouse[2]}] ===");
        }
        else
        {
            Console.WriteLine("!! SPI_SETMOUSE failed: " + Marshal.GetLastWin32Error());
        }

        SetCursorPos(original.x, original.y);
        Console.WriteLine("cursor restored");
        return 0;
    }

    private static void RunSuite(int cx, int cy, string tag)
    {
        TestRelative(20, 8, tag, cx, cy);
        TestRelative(10, 30, tag, cx, cy);
        TestAbsolute(20, 8, tag, cx, cy);
        TestSetCursorPos(20, 8, tag, cx, cy);
        TestRelativeBurst(10, 30, tag, cx, cy);
        Console.WriteLine();
    }

    private static void TestRelative(int steps, int step, string tag, int cx, int cy)
    {
        Reset(cx, cy);
        var a = Begin();
        for (int i = 0; i < steps; i++)
        {
            var inp = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = step, dy = 0, dwFlags = (int)MOUSEEVENTF_MOVE } };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(2);
        }
        End(a, steps, step, tag, $"SendInput relative {step}px x{steps}");
    }

    /// <summary>All steps queued first, then measured: models what a burst of touchpad frames looks like.</summary>
    private static void TestRelativeBurst(int steps, int step, string tag, int cx, int cy)
    {
        Reset(cx, cy);
        var a = Begin();
        for (int i = 0; i < steps; i++)
        {
            var inp = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = step, dy = 0, dwFlags = (int)MOUSEEVENTF_MOVE } };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }
        Thread.Sleep(150);
        End(a, steps, step, tag, $"SendInput burst   {step}px x{steps}");
    }

    private static void Reset(int cx, int cy)
    {
        SetCursorPos(cx, cy);
        Thread.Sleep(30);
        POINT p; GetCursorPos(out p);
        if (p.x != cx || p.y != cy) Console.WriteLine($"  !! anchor failed: wanted {cx},{cy} got {p.x},{p.y}");
    }

    private static void TestAbsolute(int steps, int step, string tag, int cx, int cy)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        Reset(cx, cy);
        var a = Begin();
        for (int i = 0; i < steps; i++)
        {
            var t = a.start;
            t.x += step * (i + 1);
            int nx = (int)((t.x - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ny = (int)((t.y - vy) * 65535.0 / Math.Max(1, vh - 1));
            var inp = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = (int)(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK) } };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(2);
        }
        End(a, steps, step, tag, $"SendInput absolute {step}px x{steps}");
    }

    private static void TestSetCursorPos(int steps, int step, string tag, int cx, int cy)
    {
        Reset(cx, cy);
        var a = Begin();
        for (int i = 0; i < steps; i++)
        {
            SetCursorPos(a.start.x + step * (i + 1), a.start.y);
        }
        End(a, steps, step, tag, $"SetCursorPos        {step}px x{steps}");
    }

    private readonly struct Anchor
    {
        public readonly POINT start;
        public readonly long t0;
        public Anchor(POINT p, long t) { start = p; t0 = t; }
    }

    private static Anchor Begin()
    {
        POINT p;
        GetCursorPos(out p);
        return new Anchor(p, Stopwatch.GetTimestamp());
    }

    private static void End(Anchor a, int steps, int step, string tag, string prim)
    {
        POINT p;
        GetCursorPos(out p);
        int requested = steps * step;
        int actual = p.x - a.start.x;
        double ms = (Stopwatch.GetTimestamp() - a.t0) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  {prim,-34} [{tag,-14}] requested=+{requested,4}px actual=+{actual,4}px ratio={(requested == 0 ? 0 : (double)actual / requested):F3} ({ms:F0} ms)");
    }

    private static string ReadReg(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            var v = k?.GetValue(name);
            if (v is byte[] b) return $"[{b.Length} bytes]";
            return v?.ToString() ?? "(absent)";
        }
        catch { return "?"; }
    }

    // ---------------------------------------------------------------- interop

    private static int[] _mouse = new int[3];

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);

    static AccelTest()
    {
        SystemParametersInfo(SPI_GETMOUSE, 0, _mouse, 0);
    }
}
