using System.Text;

namespace Mftd;

/// <summary>极简日志：事件日志立即落盘；逐帧日志攒批落盘（避免 IO 抖动）。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Buffer = new();
    private static string _path = "";
    private static bool _verbose;

    public static bool Verbose => _verbose;

    public static void Init(string path, bool verbose)
    {
        _path = path;
        _verbose = verbose;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 追加而不是截断：保留上一轮会话的线索；超过 2 MB 才轮转
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                var old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(path, old);
            }
            File.AppendAllText(path, $"[{Now()}] --- MacThreeFingerDrag start (pid {Environment.ProcessId}) ---{Environment.NewLine}");
        }
        catch { /* 日志失败不影响功能 */ }
    }

    public static void SetVerbose(bool verbose)
    {
        if (_verbose == verbose) return;
        _verbose = verbose;
        Info(verbose ? "详细日志已开启" : "详细日志已关闭");
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    public static void Info(string message)
    {
        lock (Gate) Buffer.Append('[').Append(Now()).Append("] ").Append(message).Append(Environment.NewLine);
        Write(force: true);
    }

    public static void Debug(string message)
    {
        if (!_verbose) return;
        lock (Gate) Buffer.Append('[').Append(Now()).Append("] D ").Append(message).Append(Environment.NewLine);
        Write(force: false);
    }

    /// <summary>热路径：不做字符串拼接，避免每帧分配。</summary>
    public static void DebugFrame(char tag, int n, double dx, double dy, double dt, int stepX, int stepY)
    {
        if (!_verbose) return;
        lock (Gate)
        {
            Buffer.Append('[').Append(Now()).Append("] F ").Append(tag)
                .Append(" n=").Append(n)
                .Append(" d=").Append(dx.ToString("F1")).Append(',').Append(dy.ToString("F1"))
                .Append(" dt=").Append(dt.ToString("F1"))
                .Append(" step=").Append(stepX).Append(',').Append(stepY)
                .Append(Environment.NewLine);
        }
        Write(force: false);
    }

    private static void Write(bool force)
    {
        string text;
        lock (Gate)
        {
            if (Buffer.Length == 0) return;
            if (!force && Buffer.Length < 4096) return; // 逐帧日志攒批
            text = Buffer.ToString();
            Buffer.Clear();
        }
        try { File.AppendAllText(_path, text); } catch { }
    }

    public static void Flush() => Write(force: true);
}
