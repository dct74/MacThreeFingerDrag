using Microsoft.Win32;

namespace Mftd;

/// <summary>
/// Windows 侧设置：三指手势开关（必须关掉，否则系统会先截获手势）与开机自启。
/// 手势键位置已在本机确认：HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad
/// </summary>
internal static class WindowsSettings
{
    private const string PtpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MacThreeFingerDrag";

    /// <summary>Windows 自带三指手势是否会先截获我们的手势。</summary>
    public static bool GesturesConflict()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PtpKey);
            if (k == null) return false;
            return GetInt(k, "ThreeFingerSlideEnabled") != 0 || GetInt(k, "ThreeFingerTapEnabled") != 0;
        }
        catch { return false; }
    }

    public static void FixWindowsGestures(out string report)
    {
        var lines = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PtpKey, true);
            if (k == null) { report = "无法打开 PrecisionTouchPad 注册表键。"; return; }

            void Set(string name, int value)
            {
                int before = GetInt(k, name);
                if (before == value) { lines.Add($"  {name}: 已是 {value}"); return; }
                k.SetValue(name, value, RegistryValueKind.DWord);
                lines.Add($"  {name}: {before} → {value}");
            }

            Set("ThreeFingerSlideEnabled", 0); // 三指左右滑（任务视图/切换桌面）
            Set("ThreeFingerTapEnabled", 0);   // 三指点按（搜索）
            Set("TapAndDrag", 0);              // 双击并拖动以多选（与手势冲突）
            lines.Add("");
            lines.Add("已写入。若立刻无效：打开「设置 → 蓝牙和其他设备 → 触控板」再手动确认一次，或注销/重启一次。");
        }
        catch (Exception ex)
        {
            lines.Add("写入失败: " + ex.Message);
        }
        report = "Windows 三指手势设置：\n\n" + string.Join('\n', lines);
    }

    private static int GetInt(RegistryKey k, string name)
    {
        var v = k.GetValue(name);
        return v == null ? -1 : Convert.ToInt32(v);
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on)
            {
                var exe = Environment.ProcessPath ?? "";
                k?.SetValue(RunValue, $"\"{exe}\" --autostart");
            }
            else k?.DeleteValue(RunValue, false);
        }
        catch (Exception ex) { Log.Info("设置自启失败: " + ex.Message); }
    }

    public static bool GetAutostart()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 自修复：已开启自启但注册的路径不是当前 exe（例如从 Debug 换成了 Release），
    /// 或者注册的文件已经不存在 → 重写成当前路径。
    /// </summary>
    public static void EnsureAutostartPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = k?.GetValue(RunValue) as string;
            if (current == null) return; // 本来就没开自启，不动它
            var exe = Environment.ProcessPath ?? "";
            var expected = $"\"{exe}\" --autostart";
            if (current == expected) return;
            using var w = Registry.CurrentUser.CreateSubKey(RunKey, true);
            w?.SetValue(RunValue, expected);
            Log.Info("已更新自启路径: " + expected);
        }
        catch (Exception ex) { Log.Info("修正自启路径失败: " + ex.Message); }
    }
}
