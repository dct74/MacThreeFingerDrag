namespace Mftd;

/// <summary>
/// 三指拖动状态机（参数见 DESIGN §4.3，全部按本机实测标定）。
///
///   IDLE --3指稳定>StartHoldMs 且有位移>ArmDistance--> DRAGGING（按下拖动键）
///   ARMED --手指数变化--> IDLE                       （三指点按不产生点击）
///   DRAGGING --手指数 != 3--> GRACE(ReleaseGraceMs)   （macOS 的「抬手放回继续拖」）
///   GRACE --3指回来--> DRAGGING（不重按） / 超时--> IDLE（松开）
///   兜底：250ms 收不到帧、设备热插拔、锁屏、退出 → 强制松开
/// </summary>
internal sealed class DragEngine : IFrameSink
{
    private enum State { Idle, Armed, Dragging, Grace }

    // 加速度曲线（只在屏幕像素层面，绝不与系统曲线叠加——因为位置由 SetCursorPos 钉住）
    private const double AccelMax = 1.6;      // 最快时额外乘 1.6
    private const double AccelVref = 12.0;    // 参考速度：每秒 12 个板宽
    private const double WatchdogMs = 250.0;  // 拖动中多久收不到帧就强制松开

    private readonly Config _cfg;
    private readonly Injector _inj;

    private State _state = State.Idle;
    private double _armStartMs;
    private double _graceStartMs;
    private double _lastFrameMs;
    private double _lastMoveMs;
    private double _armBaseX, _armBaseY;
    private double _prevCx, _prevCy;
    private double _velEma;
    private readonly uint[] _prevIds = new uint[16];
    private readonly int[] _prevSlots = new int[16];
    private int _prevCount = -1;
    private int _frames, _spikes;
    private double _dragStartMs;

    public StateName CurrentState => (StateName)(int)_state;
    public enum StateName { Idle, Armed, Dragging, Grace }
    public bool IsDragging => _state == State.Dragging;
    public long Frames => _frames;
    public long Spikes => _spikes;
    public long DragCount { get; private set; }
    public double LastDragMs { get; private set; }
    private double _totalDragMs;
    public double TotalDragMs => _totalDragMs;

    public DragEngine(Config cfg, Injector injector)
    {
        _cfg = cfg;
        _inj = injector;
        _inj.SetButton(cfg.Button);
    }

    public void OnTouchFrame(PtpTouchpad dev, Contact[] contacts, int count, double nowMs, int reportIndex, int reportCount)
    {
        _frames++;
        _lastFrameMs = nowMs;

        // 质心（不依赖 ContactId 连续性，对单指抖动/换指鲁棒）
        double cx = 0, cy = 0;
        for (int i = 0; i < count; i++) { cx += contacts[i].X; cy += contacts[i].Y; }
        if (count > 0) { cx /= count; cy /= count; }

        switch (_state)
        {
            case State.Idle:
                if (count == 3)
                {
                    _state = State.Armed;
                    _armStartMs = nowMs;
                    _armBaseX = cx; _armBaseY = cy;
                    Log.Debug("ARMED (3 指落下)");
                }
                break;

            case State.Armed:
                if (count != 3) { _state = State.Idle; Log.Debug("CANCEL (三指点按，不点击)"); break; }
                if (nowMs - _armStartMs < _cfg.StartHoldMs) break;
                double dist = Math.Sqrt((cx - _armBaseX) * (cx - _armBaseX) + (cy - _armBaseY) * (cy - _armBaseY));
                if (dist < _cfg.ArmDistance) break;
                StartDrag(cx, cy, contacts, count, nowMs);
                break;

            case State.Dragging:
                if (count != 3) { _state = State.Grace; _graceStartMs = nowMs; Log.Debug($"GRACE (n={count})"); break; }
                Move(cx, cy, dev, contacts, count, nowMs);
                break;

            case State.Grace:
                if (count == 3)
                {
                    // 宽限期内手指回来了：继续同一个拖动，不重新按键（macOS 的「可续拖」）
                    _state = State.Dragging;
                    _lastMoveMs = nowMs;
                    Rebaseline(cx, cy, contacts, count);
                    Log.Debug("RESUME (宽限期内续拖)");
                }
                break;
        }
    }

    private void StartDrag(double cx, double cy, Contact[] contacts, int count, double nowMs)
    {
        _inj.BeginDrag();
        _state = State.Dragging;
        _lastMoveMs = nowMs;
        _dragStartMs = nowMs;
        DragCount++;
        Rebaseline(cx, cy, contacts, count);
        Log.Info("三指拖动开始 (已按下拖动键)");
    }

    private void Rebaseline(double cx, double cy, Contact[] contacts, int count)
    {
        _prevCx = cx; _prevCy = cy;
        _velEma = 0;
        _prevCount = count;
        for (int i = 0; i < count; i++) { _prevIds[i] = contacts[i].Id; _prevSlots[i] = contacts[i].Slot; }
    }

    private bool ContactSetChanged(Contact[] contacts, int count)
    {
        if (count != _prevCount) return true;
        for (int i = 0; i < count; i++)
        {
            bool found = false;
            for (int j = 0; j < _prevCount; j++)
                if (_prevIds[j] == contacts[i].Id && _prevSlots[j] == contacts[i].Slot) { found = true; break; }
            if (!found) return true;
        }
        return false;
    }

    private void Move(double cx, double cy, PtpTouchpad dev, Contact[] contacts, int count, double nowMs)
    {
        if (ContactSetChanged(contacts, count))
        {
            // 加/减指或 ContactId 变化：本帧不移动，重新基线化（实测坏点会到 13000 单位/帧，就是这类跳变）
            Rebaseline(cx, cy, contacts, count);
            _lastMoveMs = nowMs;
            return;
        }

        double dx = cx - _prevCx, dy = cy - _prevCy;
        double mag = Math.Sqrt(dx * dx + dy * dy);
        double dt = nowMs - _lastMoveMs;
        _lastMoveMs = nowMs;
        if (dt < 4) dt = 4;              // 落指瞬间的突发帧（间隙 0.1ms）不参与速度计算
        if (dt > 50) dt = 50;

        if (mag > _cfg.SpikeLimit)
        {
            _spikes++;
            Log.Debug($"SPIKE 丢弃 (|Δ|={mag:F0} > {_cfg.SpikeLimit})");
            Rebaseline(cx, cy, contacts, count);
            return;
        }
        if (mag == 0)
        {
            LogFrameSampled('S', count, 0, 0, dt, 0, 0);
            return;
        }

        double scale = (double)_inj.ScaleRefWidth / Math.Max(1, dev.XMax) * (_cfg.Sensitivity / 100.0);
        double mult = 1.0;
        if (_cfg.Acceleration)
        {
            double padPerSec = (mag / Math.Max(1, dev.XMax)) / (dt / 1000.0);
            _velEma = _velEma * 0.6 + padPerSec * 0.4;
            double t = _velEma / AccelVref;
            if (t > 1) t = 1;
            mult = 1.0 + AccelMax * (t * t * (3 - 2 * t)); // smoothstep
        }

        double stepX = dx * scale * mult;
        double stepY = dy * scale * mult;
        _inj.MoveBy(stepX, stepY);
        _prevCx = cx; _prevCy = cy;
        LogFrameSampled('M', count, dx, dy, dt, (int)stepX, (int)stepY);
    }

    private int _logCounter;

    /// <summary>逐帧日志抽样（默认每 25 帧一条 ≈ 5 Hz），避免长时间观察把日志写爆。</summary>
    private void LogFrameSampled(char tag, int n, double dx, double dy, double dt, int stepX, int stepY)
    {
        if (!Log.Verbose) return;
        if (++_logCounter % 25 == 0)
            Log.DebugFrame(tag, n, dx, dy, dt, stepX, stepY);
    }

    /// <summary>时间驱动的状态迁移（没有帧时也要能过期）。由 200ms 定时器调用。</summary>
    public void Tick(double nowMs)
    {
        switch (_state)
        {
            case State.Armed:
                if (nowMs - _lastFrameMs > 300) _state = State.Idle; // 设备沉寂，放弃
                break;
            case State.Dragging:
                if (nowMs - _lastFrameMs > WatchdogMs) ForceRelease($"看门狗：{WatchdogMs:F0}ms 无帧");
                break;
            case State.Grace:
                if (nowMs - _graceStartMs >= _cfg.ReleaseGraceMs)
                {
                    _state = State.Idle;
                    _inj.EndDrag();
                    EndDragStats(nowMs);
                    Log.Info("拖动结束 (松开拖动键)");
                }
                break;
        }
    }

    public void ForceRelease(string reason)
    {
        if (_inj.IsDown)
        {
            _inj.EndDrag();
            EndDragStats(Program.NowMs());
            Log.Info($"强制松开拖动键: {reason}");
        }
        _state = State.Idle;
        _prevCount = -1;
    }

    private void EndDragStats(double nowMs)
    {
        LastDragMs = nowMs - _dragStartMs;
        _totalDragMs += LastDragMs;
    }

    public void OnDeviceLost() => ForceRelease("触控板设备变更/失联");
}
