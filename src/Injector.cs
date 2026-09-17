namespace Mftd;

/// <summary>
/// 光标注入。实测结论（DESIGN §2.5）：
///   · SendInput 相对移动会被系统「提高指针精确度」曲线放大（本机 30px 步长 → 1.94×）；
///   · SetCursorPos / 绝对注入精确 1:1，但 SetCursorPos 不产生 WM_MOUSEMOVE。
/// 因此采用混合：相对注入负责让目标窗口收到鼠标消息（窗口拖动/OLE 拖放需要），
/// 再用 SetCursorPos 把光标钉到我们自己维护的模型位置 → 跟手且与用户鼠标设置无关。
/// </summary>
internal sealed class Injector
{
    /// <summary>注入方式（实测用；默认 hybrid）。</summary>
    public enum Mode { Hybrid, Relative, Absolute }

    private int _modelX, _modelY;
    private double _subX, _subY;
    private bool _down;
    private int _downFlag, _upFlag;

    private int _vx, _vy, _vw, _vh;

    public Mode InjectMode { get; set; } = Mode.Hybrid;

    /// <summary>「整块板宽」映射到的像素宽度：拖动开始时取光标所在显示器宽度。</summary>
    public int ScaleRefWidth { get; private set; } = Native.GetSystemMetrics(Native.SM_CXSCREEN);

    private static string MarkerPath => Path.Combine(Config.Dir, ".dragging");

    public bool IsDown => _down;
    public int ModelX => _modelX;
    public int ModelY => _modelY;

    public Injector()
    {
        RefreshDesktopBounds();
    }

    public void RefreshDesktopBounds()
    {
        _vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        _vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
    }

    public void SetButton(string button)
    {
        (_downFlag, _upFlag) = button switch
        {
            "middle" => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            "right" => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP)
        };
    }

    /// <summary>把「整块板宽」映射到光标所在显示器的宽度（多屏时比用主屏宽度一致）。</summary>
    private void UpdateScaleReference()
    {
        try
        {
            if (!Native.GetCursorPos(out var p)) return;
            var mon = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(mon, ref mi))
            {
                int w = mi.rcMonitor.right - mi.rcMonitor.left;
                if (w > 100) ScaleRefWidth = w;
            }
            else ScaleRefWidth = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        }
        catch { ScaleRefWidth = Native.GetSystemMetrics(Native.SM_CXSCREEN); }
    }

    /// <summary>
    /// 上一个实例如果在拖动中挂掉（任务管理器强杀/崩溃），拖动键会残留在按下状态。
    /// 启动时用标记文件检测并补发一次抬起。
    /// </summary>
    public static bool RecoverStuckButton()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return false;
            File.Delete(MarkerPath);
            SendButton(Native.MOUSEEVENTF_LEFTUP);
            SendButton(Native.MOUSEEVENTF_MIDDLEUP);
            SendButton(Native.MOUSEEVENTF_RIGHTUP);
            return true;
        }
        catch { return false; }
    }

    /// <summary>按下拖动键，并把光标模型对齐到当前真实光标位置。</summary>
    public void BeginDrag()
    {
        if (_down) return;
        UpdateScaleReference();
        if (Native.GetCursorPos(out var p)) { _modelX = p.x; _modelY = p.y; }
        _subX = _subY = 0;
        SendButton(_downFlag);
        _down = true;
        try { File.WriteAllText(MarkerPath, Environment.ProcessId.ToString()); } catch { }
    }
    public void EndDrag()
    {
        if (!_down) return;
        SendButton(_upFlag);
        _down = false;
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
    }

    /// <summary>按屏幕像素位移移动光标（含子像素累加与虚拟桌面钳制）。</summary>
    public void MoveBy(double dxPx, double dyPx)
    {
        _subX += dxPx;
        _subY += dyPx;
        int sx = (int)_subX, sy = (int)_subY;
        _subX -= sx; _subY -= sy;
        if (sx == 0 && sy == 0) return;

        int nx = Math.Clamp(_modelX + sx, _vx, _vx + _vw - 1);
        int ny = Math.Clamp(_modelY + sy, _vy, _vy + _vh - 1);
        int ax = nx - _modelX, ay = ny - _modelY;
        _modelX = nx; _modelY = ny;
        if (ax == 0 && ay == 0) return;

        switch (InjectMode)
        {
            case Mode.Relative:
                SendMoveRelative(ax, ay);
                break;
            case Mode.Absolute:
                SendMoveAbsolute(_modelX, _modelY);
                break;
            default: // Hybrid：相对消息给目标窗口 + SetCursorPos 钉住位置（不受系统加速影响）
                SendMoveRelative(ax, ay);
                Native.SetCursorPos(_modelX, _modelY);
                break;
        }
    }

    private static void SendMoveRelative(int dx, int dy)
    {
        var input = new Native.INPUT[1];
        input[0].type = Native.INPUT_MOUSE;
        input[0].mi.dx = dx;
        input[0].mi.dy = dy;
        input[0].mi.dwFlags = Native.MOUSEEVENTF_MOVE;
        Native.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>绝对注入（虚拟桌面归一化坐标）：不受系统指针加速影响，消息点 == 模型位置。</summary>
    private void SendMoveAbsolute(int x, int y)
    {
        int nx = (int)Math.Round((x - _vx) * 65535.0 / Math.Max(1, _vw - 1));
        int ny = (int)Math.Round((y - _vy) * 65535.0 / Math.Max(1, _vh - 1));
        var input = new Native.INPUT[1];
        input[0].type = Native.INPUT_MOUSE;
        input[0].mi.dx = nx;
        input[0].mi.dy = ny;
        input[0].mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        Native.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private static void SendButton(int flag)
    {
        var input = new Native.INPUT[1];
        input[0].type = Native.INPUT_MOUSE;
        input[0].mi.dwFlags = flag;
        uint sent = Native.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        if (sent == 0)
            Log.Info($"SendInput 失败 (flag=0x{flag:X}) err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }
}
