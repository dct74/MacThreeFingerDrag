using System.Globalization;

namespace Mftd;

/// <summary>
/// 配置：%APPDATA%\MacThreeFingerDrag\config.json。
/// 格式是「每行一个键」的扁平 JSON —— 既可以直接手改，也能被我们的小解析器无反射读取（AOT 安全）。
/// </summary>
internal sealed class Config
{
    public bool Enabled = true;
    public int Sensitivity = 100;        // 100 = 整块板宽 ≈ 一屏宽
    public bool Acceleration = true;
    public string Button = "left";       // left / middle / right
    public string InjectMode = "hybrid"; // hybrid（默认）/ relative / absolute —— 跟手与消息语义的实验开关
    public int ReleaseGraceMs = 300;     // 抬手后仍保持拖动的宽限期（macOS 的「可续拖」）
    public int StartHoldMs = 25;         // 三指需稳定按住多久才认为要拖动
    public int ArmDistance = 30;         // 开始拖动所需位移（逻辑单位，约 0.3mm）
    public int SpikeLimit = 2000;        // 单帧位移上限，超过视为坏点（单位：逻辑单位/帧）
    public bool Autostart = false;
    public bool Debug = false;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacThreeFingerDrag");
    public static string DefaultPath => Path.Combine(Dir, "config.json");
    public static string LogPath => Path.Combine(Dir, "MacThreeFingerDrag.log");

    public static Config Load(string path)
    {
        var cfg = new Config();
        LoadInto(cfg, path);
        if (!File.Exists(path)) cfg.Save(path);
        return cfg;
    }

    /// <summary>把磁盘上的配置读进已有实例（引擎持有同一个 Config 引用，所以不能换对象）。</summary>
    public static void LoadInto(Config cfg, string path)
    {
        try
        {
            if (!File.Exists(path)) { cfg.Normalize(); return; }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("//") || line.Length == 0 || line == "{" || line == "}") continue;
                if (line[0] != '"') continue;
                int end = line.IndexOf('"', 1);
                if (end < 0) continue;
                var key = line.Substring(1, end - 1);
                int colon = line.IndexOf(':', end);
                if (colon < 0) continue;
                var value = line.Substring(colon + 1).Trim().TrimEnd(',').Trim();
                Apply(cfg, key, value);
            }
        }
        catch { /* 配置错误 → 保留现有值 */ }
        cfg.Normalize();
        try { cfg.LastWrite = File.GetLastWriteTimeUtc(path); } catch { }
    }

    private static void Apply(Config c, string key, string value)
    {
        bool b = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        switch (key)
        {
            case "enabled": c.Enabled = b; break;
            case "sensitivity": c.Sensitivity = ToInt(value, c.Sensitivity); break;
            case "acceleration": c.Acceleration = b; break;
            case "button": c.Button = value.Trim('"').ToLowerInvariant(); break;
            case "injectMode": c.InjectMode = value.Trim('"').ToLowerInvariant(); break;
            case "releaseGraceMs": c.ReleaseGraceMs = ToInt(value, c.ReleaseGraceMs); break;
            case "startHoldMs": c.StartHoldMs = ToInt(value, c.StartHoldMs); break;
            case "armDistance": c.ArmDistance = ToInt(value, c.ArmDistance); break;
            case "spikeLimit": c.SpikeLimit = ToInt(value, c.SpikeLimit); break;
            case "autostart": c.Autostart = b; break;
            case "debug": c.Debug = b; break;
        }
    }

    private static int ToInt(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void Normalize()
    {
        Sensitivity = Math.Clamp(Sensitivity, 20, 600);
        ReleaseGraceMs = Math.Clamp(ReleaseGraceMs, 0, 1500);
        StartHoldMs = Math.Clamp(StartHoldMs, 0, 200);
        ArmDistance = Math.Clamp(ArmDistance, 0, 500);
        SpikeLimit = Math.Clamp(SpikeLimit, 100, 20000);
        if (Button != "left" && Button != "middle" && Button != "right") Button = "left";
        if (InjectMode != "hybrid" && InjectMode != "relative" && InjectMode != "absolute") InjectMode = "hybrid";
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"enabled\": {B(Enabled)},");
            sb.AppendLine($"  \"sensitivity\": {Sensitivity},");
            sb.AppendLine($"  \"acceleration\": {B(Acceleration)},");
            sb.AppendLine($"  \"button\": \"{Button}\",");
            sb.AppendLine($"  \"injectMode\": \"{InjectMode}\",");
            sb.AppendLine($"  \"releaseGraceMs\": {ReleaseGraceMs},");
            sb.AppendLine($"  \"startHoldMs\": {StartHoldMs},");
            sb.AppendLine($"  \"armDistance\": {ArmDistance},");
            sb.AppendLine($"  \"spikeLimit\": {SpikeLimit},");
            sb.AppendLine($"  \"autostart\": {B(Autostart)},");
            sb.AppendLine($"  \"debug\": {B(Debug)}");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            LastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) { Log.Info("保存配置失败: " + ex.Message); }
    }

    private static string B(bool v) => v ? "true" : "false";

    public DateTime LastWrite = DateTime.MinValue;

    public bool ChangedOnDisk(string path)
    {
        try { return File.Exists(path) && File.GetLastWriteTimeUtc(path) != LastWrite; }
        catch { return false; }
    }
}
