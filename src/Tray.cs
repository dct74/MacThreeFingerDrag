using System.Runtime.InteropServices;

namespace Mftd;

/// <summary>原生托盘图标 + 弹出菜单（不用 WinForms / WinUI）。</summary>
internal sealed class Tray : IDisposable
{
    private const int CmdToggleEnabled = 1;
    private const int CmdSensUp = 2;
    private const int CmdSensDown = 3;
    private const int CmdAccel = 4;
    private const int CmdButton = 5;
    private const int CmdInject = 15;
    private const int CmdGrace = 6;
    private const int CmdAutostart = 7;
    private const int CmdFixGestures = 8;
    private const int CmdOpenConfig = 9;
    private const int CmdOpenLog = 10;
    private const int CmdVerbose = 11;
    private const int CmdRelaunch = 12;
    private const int CmdStats = 16;
    private const int CmdAbout = 13;
    private const int CmdQuit = 14;

    private readonly App _app;
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private Native.NOTIFYICONDATA _nid;

    public Tray(App app, IntPtr hwnd)
    {
        _app = app;
        _hwnd = hwnd;
        _icon = MakeIcon(32);
        AddIcon();
    }

    public void AddIcon()
    {        _nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP,
            uCallbackMessage = Native.TRAY_CALLBACK,
            hIcon = _icon,
            szTip = $"三指拖动 v{AppInfo.Version}",
            szInfo = "",
            szInfoTitle = ""
        };
        if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref _nid))
            Log.Info("Shell_NotifyIcon(NIM_ADD) 失败 err=" + Marshal.GetLastWin32Error());
        else
            Log.Info($"托盘图标已创建 (hIcon=0x{_icon.ToInt64():X}, cbSize={_nid.cbSize})");
    }

    /// <summary>Explorer 重启后需要重新添加图标。</summary>
    public void Recreate()
    {
        Log.Info("Explorer 重启，重新添加托盘图标");
        AddIcon();
    }

    public void UpdateTooltip(string text)
    {
        _nid.uFlags = Native.NIF_TIP;
        _nid.szTip = text.Length > 120 ? text.Substring(0, 120) : text;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref _nid);
    }

    /// <summary>气泡提醒（首次运行 / 手势冲突检测）。</summary>
    public void ShowBalloon(string title, string text)
    {
        _nid.uFlags = Native.NIF_INFO;
        _nid.szInfoTitle = title;
        _nid.szInfo = text;
        _nid.dwInfoFlags = 0x00000001; // NIIF_INFO
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref _nid);
    }

    public void ShowMenu()
    {
        var cfg = _app.Cfg;
        var menu = Native.CreatePopupMenu();
        try
        {
            Append(menu, CmdToggleEnabled, "启用三指拖动", cfg.Enabled);
            Separator(menu);
            Append(menu, CmdSensUp, $"灵敏度 +10   (当前 {cfg.Sensitivity})", false);
            Append(menu, CmdSensDown, "灵敏度 -10", false);
            Append(menu, CmdAccel, "指针加速", cfg.Acceleration);
            Append(menu, CmdButton, $"拖动按键: {ButtonLabel(cfg.Button)}", false);
            Append(menu, CmdInject, $"注入方式: {cfg.InjectMode}", false);
            Append(menu, CmdGrace, $"抬手宽限期: {cfg.ReleaseGraceMs} ms", false);
            Separator(menu);
            Append(menu, CmdAutostart, "随登录启动", cfg.Autostart);
            Append(menu, CmdFixGestures, "修复 Windows 三指手势设置", false);
            Separator(menu);
            Append(menu, CmdOpenConfig, "打开配置文件", false);
            Append(menu, CmdOpenLog, "打开日志", false);
            Append(menu, CmdVerbose, "详细日志 (调试)", cfg.Debug);
            Append(menu, CmdRelaunch, "以管理员身份重启…", false);
            Separator(menu);
            AppendDisabled(menu, _app.StatusText);
            Append(menu, CmdStats, "统计信息…", false);
            Append(menu, CmdAbout, "关于 / 使用说明", false);
            Append(menu, CmdQuit, "退出", false);

            Native.GetCursorPos(out var pt);
            Native.SetForegroundWindow(_hwnd); // 让菜单能正常消失
            int cmd = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, pt.x, pt.y, _hwnd, IntPtr.Zero);
            if (cmd != 0) Execute(cmd);
        }
        finally { Native.DestroyMenu(menu); }
    }

    private static string ButtonLabel(string b) => b switch
    {
        "middle" => "中键",
        "right" => "右键",
        _ => "左键"
    };

    private void Execute(int cmd)
    {
        var cfg = _app.Cfg;
        switch (cmd)
        {
            case CmdToggleEnabled:
                cfg.Enabled = !cfg.Enabled;
                if (!cfg.Enabled) _app.Engine.ForceRelease("已暂停");
                break;
            case CmdSensUp: cfg.Sensitivity = Math.Clamp(cfg.Sensitivity + 10, 20, 600); break;
            case CmdSensDown: cfg.Sensitivity = Math.Clamp(cfg.Sensitivity - 10, 20, 600); break;
            case CmdAccel: cfg.Acceleration = !cfg.Acceleration; break;
            case CmdButton:
                cfg.Button = cfg.Button switch { "left" => "middle", "middle" => "right", _ => "left" };
                _app.Injector.SetButton(cfg.Button);
                break;
            case CmdInject:
                cfg.InjectMode = cfg.InjectMode switch { "hybrid" => "relative", "relative" => "absolute", _ => "hybrid" };
                break;
            case CmdGrace:
                cfg.ReleaseGraceMs = cfg.ReleaseGraceMs switch
                {
                    <= 150 => 300,
                    <= 300 => 500,
                    <= 500 => 800,
                    _ => 150
                };
                break;
            case CmdAutostart:
                cfg.Autostart = !cfg.Autostart;
                WindowsSettings.SetAutostart(cfg.Autostart);
                break;
            case CmdFixGestures:
                WindowsSettings.FixWindowsGestures(out var report);
                Native.MessageBox(_hwnd, report, "Windows 三指手势设置", Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_SETFOREGROUND);
                break;
            case CmdOpenConfig:
                cfg.Save(Config.DefaultPath);
                Native.ShellExecute(_hwnd, "open", Config.DefaultPath, null, null, 1);
                break;
            case CmdOpenLog:
                Log.Flush();
                Native.ShellExecute(_hwnd, "open", Config.LogPath, null, null, 1);
                break;
            case CmdVerbose:
                cfg.Debug = !cfg.Debug;
                Log.SetVerbose(cfg.Debug);
                break;
            case CmdRelaunch:
                _app.RelaunchElevated();
                return;
            case CmdStats:
                Native.MessageBox(_hwnd, _app.StatsText(), "三指拖动 · 运行统计",
                    Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_SETFOREGROUND);
                return;
            case CmdAbout:
                Native.MessageBox(_hwnd, AboutText(), "关于 三指拖动", Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_SETFOREGROUND);
                break;
            case CmdQuit:
                _app.Shutdown();
                return;
        }
        cfg.Save(Config.DefaultPath);
        _app.ApplyConfig();
    }

    private static string AboutText() =>
        $"MacThreeFingerDrag v{AppInfo.Version} —— 在 Windows 上复刻 macOS 的三指拖动。\n\n" +
        "用法：三根手指放在触控板上移动 = 拖动窗口 / 选中文本；\n" +
        "      三指点按不会产生点击；抬手后约 300ms 内放回手指可以继续拖动。\n\n" +
        "前提：需要把 Windows 的「三指手势」设为「无」，否则系统会先截获手势。\n" +
        "      菜单里的「修复 Windows 三指手势设置」可一键完成（可能需要注销一次）。\n\n" +
        "配置文件: " + Config.DefaultPath + "\n" +
        "日志: " + Config.LogPath + "\n" +
        "项目主页: " + AppInfo.RepoUrl + "\n\n" +
        "参数依据见 docs/DESIGN.zh-CN.md（基于 2 万余帧实测标定）。";

    private static void Append(IntPtr menu, int id, string text, bool check)
    {
        uint flags = Native.MF_STRING | (check ? Native.MF_CHECKED : 0);
        Native.AppendMenu(menu, flags, id, text);
    }

    private static void AppendDisabled(IntPtr menu, string text)
    {
        Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, 0, text);
    }

    private static void Separator(IntPtr menu) => Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);

    // ---------------- 图标（运行时矢量绘制，无需二进制资源） ----------------

    private static IntPtr MakeIcon(int size)
    {
        var pixels = new uint[size * size];
        var bg = (r: 0x1F, g: 0x6F, b: 0xEB);
        const double s = 32.0;
        double k = size / s;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double covBg = 0, covBar = 0;
                for (int sy = 0; sy < 3; sy++)
                    for (int sx = 0; sx < 3; sx++)
                    {
                        double px = (x + (sx + 0.5) / 3.0) / k;
                        double py = (y + (sy + 0.5) / 3.0) / k;
                        if (InRoundRect(px, py, 1.5, 1.5, 29, 29, 6)) covBg += 1;
                        double barY0 = 7, barY1 = 23;
                        if (px >= 9.5 && px <= 13 && py >= barY0 && py <= barY1) covBar += 1;
                        else if (px >= 14.5 && px <= 18 && py >= barY0 - 1 && py <= barY1) covBar += 1;
                        else if (px >= 19.5 && px <= 23 && py >= barY0 && py <= barY1) covBar += 1;
                    }
                double ab = covBg / 9.0, ar = covBar / 9.0;
                if (ab <= 0 && ar <= 0) { pixels[y * size + x] = 0; continue; }

                // 先铺背景，再叠白色手指（白覆盖按子采样比例）
                double r = bg.r, g = bg.g, b = bg.b;
                double a = ab;
                if (ar > 0)
                {
                    double t = Math.Min(1.0, ar / Math.Max(ab, 1e-6));
                    r = r * (1 - t) + 255 * t;
                    g = g * (1 - t) + 255 * t;
                    b = b * (1 - t) + 255 * t;
                }
                byte A = (byte)(Math.Clamp(a, 0, 1) * 255);
                pixels[y * size + x] = (uint)(A << 24 | (byte)r << 16 | (byte)g << 8 | (byte)b);
            }
        }

        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // 自顶向下
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        IntPtr hdc = Native.GetDC(IntPtr.Zero);
        IntPtr bits;
        IntPtr color = Native.CreateDIBSection(hdc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        try
        {
            if (color != IntPtr.Zero && bits != IntPtr.Zero)
            {
                unsafe
                {
                    var dst = (uint*)bits;
                    for (int i = 0; i < pixels.Length; i++) dst[i] = pixels[i];
                }
            }
        }
        finally { Native.ReleaseDC(IntPtr.Zero, hdc); }

        IntPtr mask = Native.CreateBitmap(size, size, 1, 1, new byte[size * size / 8]);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = Native.CreateIconIndirect(ref info);
        if (mask != IntPtr.Zero) Native.DeleteObject(mask);
        if (color != IntPtr.Zero) Native.DeleteObject(color);
        if (icon == IntPtr.Zero) Log.Info("CreateIconIndirect 失败 err=" + Marshal.GetLastWin32Error());
        return icon;
    }

    private static bool InRoundRect(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        double cx = Math.Clamp(x, left + radius, right - radius);
        double cy = Math.Clamp(y, top + radius, bottom - radius);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    public void Dispose()
    {
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref _nid);
        if (_icon != IntPtr.Zero) { Native.DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
