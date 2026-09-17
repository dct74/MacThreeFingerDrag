using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mftd;

internal static class Program
{
    private const string WindowClass = "MacThreeFingerDragWnd";
    private const string MutexName = @"Local\MacThreeFingerDrag_SingleInstance";
    private const uint TimerWatchdog = 1;
    private const uint TimerConfig = 2;
    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203;

    private static App? _app;
    private static Native.WndProc? _wndProc;   // 必须保持引用，否则委托会被 GC 回收
    private static uint _taskbarCreated;

    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    [STAThread]
    private static int Main(string[] args)
    {
        // 构建期工具：生成 exe 图标资源（不启动任何窗口）
        int mk = Array.IndexOf(args, "--make-icon");
        if (mk >= 0 && mk + 1 < args.Length)
        {
            var path = args[mk + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            IconArt.WriteIco(path, new[] { 16, 24, 32, 48, 64, 128, 256 });
            Console.WriteLine("wrote " + path);
            return 0;
        }

        bool verbose = args.Contains("--debug");
        Log.Init(Config.LogPath, verbose);

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // 已有实例：让它把菜单弹出来
            var existing = Native.FindWindow(WindowClass, null);
            if (existing != IntPtr.Zero) Native.PostMessage(existing, Native.WM_APP_SHOWMENU, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        var instance = Native.GetModuleHandle(null);
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = WindowClass
        };
        if (Native.RegisterClassEx(ref wc) == 0)
            Log.Info("RegisterClassEx 失败 err=" + Marshal.GetLastWin32Error());

        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        // 后台工具最怕「静默死掉」：任何未处理异常都要落到日志里
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Info("未处理异常: " + e.ExceptionObject);

        _app = new App();
        try
        {
            if (!_app.Initialize(instance)) return 1;
            _app.Run();
        }
        catch (Exception ex)
        {
            Log.Info("致命错误: " + ex);
            try { _app.Shutdown(); } catch { }
            return 1;
        }
        return 0;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var app = _app;
        if (app == null) return Native.DefWindowProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case Native.WM_INPUT:
                app.OnRawInput(lParam);
                return IntPtr.Zero;

            case Native.WM_INPUT_DEVICE_CHANGE:
                app.OnDeviceChange();
                return IntPtr.Zero;

            case Native.WM_TIMER:
                if ((ulong)wParam == TimerWatchdog) app.Engine.Tick(NowMs());
                else if ((ulong)wParam == TimerConfig) app.CheckConfigReload();
                return IntPtr.Zero;

            case Native.TRAY_CALLBACK:
            {
                int mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse is (int)WM_LBUTTONUP or (int)WM_RBUTTONUP or (int)WM_LBUTTONDBLCLK or (int)WM_CONTEXTMENU)
                    app.Tray.ShowMenu();
                return IntPtr.Zero;
            }

            case Native.WM_APP_SHOWMENU:
                app.Tray.ShowMenu();
                return IntPtr.Zero;

            case Native.WM_QUERYENDSESSION:
                app.Engine.ForceRelease("系统注销/关机");
                return new IntPtr(1);

            case Native.WM_ENDSESSION:
                if (wParam != IntPtr.Zero) app.Engine.ForceRelease("会话结束");
                return IntPtr.Zero;

            case Native.WM_WTSSESSION_CHANGE:
                if ((ulong)wParam is Native.WTS_SESSION_LOCK or Native.WTS_SESSION_LOGOFF)
                    app.Engine.ForceRelease("锁屏/注销");
                return IntPtr.Zero;

            case Native.WM_POWERBROADCAST:
                if ((ulong)wParam == Native.PBT_APMSUSPEND) app.Engine.ForceRelease("休眠");
                return IntPtr.Zero;

            case 0x007E: // WM_DISPLAYCHANGE
                app.Injector.RefreshDesktopBounds();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                app.Shutdown();
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.KillTimer(hWnd, (IntPtr)TimerWatchdog);
                Native.KillTimer(hWnd, (IntPtr)TimerConfig);
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            app.Tray.Recreate();
            return IntPtr.Zero;
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}

internal sealed class App
{
    public Config Cfg = new();
    public Injector Injector = new();
    public DragEngine Engine = null!;
    public TouchpadManager Tp = new();
    public Tray Tray = null!;

    private IntPtr _hwndMsg;
    private IntPtr _hwndTray;
    private bool _shuttingDown;
    private int _slowTicks;
    private bool _warnedGestures;

    /// <summary>供菜单「统计信息」展示，也方便反馈问题时直接贴出来。</summary>
    public string StatsText()
    {
        var dev = Tp.Primary;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("设备: " + (dev?.ShortName ?? "未检测到"));
        if (dev != null)
            sb.AppendLine($"  触点槽={dev.SlotCount}  X量程=0..{dev.XMax}  Y量程=0..{dev.YMax}  TipSwitch={dev.HasTipSwitch}");
        sb.AppendLine($"解析: 帧={Tp.FramesParsed}  解析失败={Tp.ParseFailures}");
        sb.AppendLine($"拖动: 次数={Engine.DragCount}  本次={Engine.LastDragMs:F0}ms  累计={Engine.TotalDragMs / 1000.0:F1}s");
        sb.AppendLine($"异常帧丢弃(坏点)={Engine.Spikes}");
        sb.AppendLine();
        sb.AppendLine($"参数: 启用={Cfg.Enabled} 灵敏度={Cfg.Sensitivity} 加速={Cfg.Acceleration} " +
                      $"按键={Cfg.Button} 注入={Cfg.InjectMode} 宽限={Cfg.ReleaseGraceMs}ms");
        sb.AppendLine($"Windows 三指手势: {(WindowsSettings.GesturesConflict() ? "仍开启（建议修复）" : "已关闭")}");
        sb.AppendLine($"自启: {(WindowsSettings.GetAutostart() ? "已开启" : "未开启")}");
        sb.AppendLine();
        sb.AppendLine("配置: " + Config.DefaultPath);
        sb.AppendLine("日志: " + Config.LogPath);
        return sb.ToString();
    }

    public string StatusText
    {
        get
        {
            if (!Cfg.Enabled) return "状态: 已暂停";
            if (Tp.Primary == null) return "状态: 未检测到 Precision Touchpad";
            if (Engine.IsDragging) return "状态: 拖动中";
            return "状态: 就绪";
        }
    }

    public bool Initialize(IntPtr instance)
    {
        bool firstRun = !File.Exists(Config.DefaultPath);
        Cfg = Config.Load(Config.DefaultPath);

        // 消息专用窗口：收 Raw Input 与定时器（已验证可收到 WM_INPUT）
        _hwndMsg = Native.CreateWindowEx(0, "MacThreeFingerDragWnd", "mftd-msg", 0,
            0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        // 隐藏顶层窗口：托盘图标与弹出菜单需要它
        _hwndTray = Native.CreateWindowEx(0x00000080 /*WS_EX_TOOLWINDOW*/, "MacThreeFingerDragWnd", "mftd-tray",
            0x80000000 /*WS_POPUP*/, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwndMsg == IntPtr.Zero || _hwndTray == IntPtr.Zero)
        {
            Log.Info("创建窗口失败");
            return false;
        }

        Log.SetVerbose(Cfg.Debug);
        Engine = new DragEngine(Cfg, Injector);
        Injector.SetButton(Cfg.Button);
        Injector.InjectMode = ParseInjectMode(Cfg.InjectMode);
        Injector.RefreshDesktopBounds();

        bool ok = Tp.Register(_hwndMsg);
        Tp.Refresh();

        Tray = new Tray(this, _hwndTray);
        Native.WTSRegisterSessionNotification(_hwndMsg, Native.NOTIFY_FOR_THIS_SESSION);
        Native.SetTimer(_hwndMsg, (IntPtr)1, 200, IntPtr.Zero);
        Native.SetTimer(_hwndMsg, (IntPtr)2, 1000, IntPtr.Zero);
        _ = ok;

        if (Injector.RecoverStuckButton())
        {
            Log.Info("上一个实例在拖动中退出，已补发一次按键抬起（避免拖动键残留）");
            Native.MessageBox(IntPtr.Zero,
                "检测到上一次运行时异常退出，可能残留了按下的鼠标键。\n已自动补发一次「抬起」，现在应该正常了。",
                "三指拖动", Native.MB_OK | Native.MB_ICONWARNING | Native.MB_SETFOREGROUND);
        }
        WindowsSettings.EnsureAutostartPath();

        if (WindowsSettings.GesturesConflict())
        {
            Log.Info("检测到 Windows 三指手势仍处于开启状态，可能与本工具冲突");
            Tray.UpdateTooltip("三指拖动 —— 建议在托盘菜单里执行「修复 Windows 三指手势设置」");
        }
        Tray.UpdateTooltip(StatusText);
        if (firstRun)
            Tray.ShowBalloon("三指拖动已启动", "三根手指放在触控板上移动即可拖动窗口；三指点按不会产生点击。");
        return true;
    }

    public void Run()
    {
        Log.Info($"就绪: {StatusText}");
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        Log.Info("退出");
        Log.Flush();
    }

    public void OnRawInput(IntPtr lParam)
    {
        if (!Cfg.Enabled || Tp.Primary == null) return;
        try
        {
            Tp.HandleRawInput(lParam, Engine, Program.NowMs());
        }
        catch (Exception ex)
        {
            // 热路径绝不把异常留给系统，更不能残留按下的键
            Engine.ForceRelease("解析异常");
            Log.Info("解析异常: " + ex.Message);
        }
    }

    public void OnDeviceChange()
    {
        Engine.OnDeviceLost();
        Tp.Refresh();
        Tray.UpdateTooltip(StatusText);
    }

    public void ApplyConfig()
    {
        Injector.SetButton(Cfg.Button);
        Injector.InjectMode = ParseInjectMode(Cfg.InjectMode);
        Injector.RefreshDesktopBounds();        Log.SetVerbose(Cfg.Debug);
        Tray.UpdateTooltip(StatusText);
        Log.Info($"配置已应用: 启用={Cfg.Enabled} 灵敏度={Cfg.Sensitivity} 加速={Cfg.Acceleration} 按键={Cfg.Button} 注入={Cfg.InjectMode} 宽限={Cfg.ReleaseGraceMs}ms");
    }

    public void CheckConfigReload()
    {
        if (!Cfg.ChangedOnDisk(Config.DefaultPath))
        {
            // 顺便定期核查 Windows 三指手势是否被重新打开（系统更新/设置面板可能改回去）
            if (++_slowTicks >= 60 && !_warnedGestures && WindowsSettings.GesturesConflict())
            {
                _warnedGestures = true;
                Log.Info("Windows 三指手势被重新打开，可能与本工具冲突");
                Tray.ShowBalloon("提示", "Windows 三指手势被重新打开，建议在托盘菜单执行「修复 Windows 三指手势设置」。");
                Tray.UpdateTooltip("三指拖动 —— 建议执行「修复 Windows 三指手势设置」");
            }
            return;
        }
        Config.LoadInto(Cfg, Config.DefaultPath);
        ApplyConfig();
    }

    private static Injector.Mode ParseInjectMode(string mode) => mode switch
    {
        "relative" => Injector.Mode.Relative,
        "absolute" => Injector.Mode.Absolute,
        _ => Injector.Mode.Hybrid
    };

    public void RelaunchElevated()
    {
        // 必须先退出（释放单实例 mutex），否则新进程会以为已有实例而直接退出
        var exe = Environment.ProcessPath ?? "";
        Shutdown();
        try { Native.ShellExecute(IntPtr.Zero, "runas", exe, "--elevated", null, 1); }
        catch (Exception ex) { Log.Info("提权重启失败: " + ex.Message); }
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        Engine.ForceRelease("退出");
        Tray?.Dispose();
        Log.Flush();
        Native.DestroyWindow(_hwndTray);
        Native.DestroyWindow(_hwndMsg);
    }
}
