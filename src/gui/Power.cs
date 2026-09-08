using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LockscreenGui;

/// <summary>
/// 电源设置：直接调用 powercfg.exe（替代原 Node 后端逻辑）。
/// 一次 powercfg /q SCHEME_CURRENT 解析全部设置（GUID 别名定位行），比单点查询快。
/// </summary>
static class PowerManager
{
    public static readonly (string key, string name, string icon, string desc, string sub, string setting)[] Items =
    {
        ("lock", "锁屏后黑屏", "🔒", "锁屏壁纸出现后，多久自动关闭显示器（黑屏）。设为「永不」= 锁屏后屏幕一直亮着。", "SUB_VIDEO", "VIDEOCONLOCK"),
        ("display", "关闭显示器", "🖥️", "无任何操作闲置多久后关闭屏幕。", "SUB_VIDEO", "VIDEOIDLE"),
        ("sleep", "睡眠", "😴", "闲置多久后进入睡眠。睡眠唤醒后仍需登录，也会看到锁屏界面。", "SUB_SLEEP", "STANDBYIDLE"),
        ("hibernate", "休眠", "💤", "闲置多久后进入休眠。需系统已开启休眠功能，否则此项不会生效。", "SUB_SLEEP", "HIBERNATEIDLE")
    };

    public static readonly string[] Keys = { "lock", "display", "sleep", "hibernate" };

    public sealed record ItemState(int? Ac, int? Dc)
    {
        public string AcText => FormatSeconds(Ac ?? 0);
        public string DcText => FormatSeconds(Dc ?? 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>是否有电池（BatteryFlag 的 0x80 = 无系统电池）。替代原 Node 的 Get-CimInstance 查询。</summary>
    public static bool HasBattery()
    {
        try
        {
            if (GetSystemPowerStatus(out var st)) return (st.BatteryFlag & 0x80) == 0;
        }
        catch { }
        return false;
    }

    /// <summary>休眠功能是否可用（hiberfil.sys 是否存在）。</summary>
    public static bool HibernateAvailable()
    {
        try
        {
            string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return File.Exists(drive + @"\hiberfil.sys");
        }
        catch { return false; }
    }

    static int? ToNum(string? x)
    {
        if (string.IsNullOrWhiteSpace(x)) return null;
        long v;
        if (x.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(x.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v)) return null;
        }
        else if (!long.TryParse(x, out v)) return null;
        return v >= 0xfffffffeL ? 0 : (int)v;   // 0xffffffff = 永不
    }

    public static string FormatSeconds(int sec)
    {
        if (sec <= 0) return "永不";
        if (sec < 60) return sec + " 秒";
        if (sec < 3600) { var m = sec / 60.0; return (m == Math.Floor(m) ? ((int)m).ToString() : m.ToString("0.#")) + " 分钟"; }
        var h = sec / 3600.0;
        return (h == Math.Floor(h) ? ((int)h).ToString() : h.ToString("0.#")) + " 小时";
    }

    public static (string guid, string name) ActiveScheme()
    {
        string? t = Run.Exec("powercfg", "/getactivescheme") ?? "";
        var m = Regex.Match(t, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        string guid = m.Success ? m.Groups[1].Value : "SCHEME_CURRENT";
        var m2 = Regex.Match(t, @"GUID:\s*[0-9a-fA-F-]{36}\s*\(([^)]*)\)");
        string name = m2.Success ? m2.Groups[1].Value.Trim() : "未知";
        if (name == "未知")
        {
            var m3 = Regex.Match(t, @"GUID:\s*[0-9a-fA-F-]{36}\s*\(?\s*(.+?)\s*$", RegexOptions.Multiline);
            if (m3.Success) name = m3.Groups[1].Value.Trim();
        }
        return (guid, name);
    }

    /// <summary>一次 powercfg /q 拿回全部设置（GUID 别名 → AC/DC 索引值）。</summary>
    public static Dictionary<string, (int? ac, int? dc)> QueryAll()
    {
        var map = new Dictionary<string, (int?, int?)>(StringComparer.OrdinalIgnoreCase);
        string? t = Run.Exec("powercfg", "/q", "SCHEME_CURRENT");
        if (t == null) return map;
        string? cur = null;
        foreach (var line in t.Split('\n'))
        {
            var m = Regex.Match(line, @"GUID\s*(?:别名|Alias)\s*:\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success) { cur = m.Groups[1].Value; continue; }
            if (Regex.IsMatch(line, @"当前交流电源设置索引|Current AC Power Setting Index", RegexOptions.IgnoreCase))
            {
                var vm = Regex.Match(line, @"(0x[0-9a-fA-F]+|\d+)\s*$");
                if (cur != null && vm.Success)
                {
                    (int? ac, int? dc) old = (null, null);
                    if (map.TryGetValue(cur, out var o)) old = o;
                    map[cur] = (ToNum(vm.Groups[1].Value), old.dc);
                }
                continue;
            }
            if (Regex.IsMatch(line, @"当前直流电源设置索引|Current DC Power Setting Index", RegexOptions.IgnoreCase))
            {
                var vm = Regex.Match(line, @"(0x[0-9a-fA-F]+|\d+)\s*$");
                if (cur != null && vm.Success)
                {
                    (int? ac, int? dc) old = (null, null);
                    if (map.TryGetValue(cur, out var o)) old = o;
                    map[cur] = (old.ac, ToNum(vm.Groups[1].Value));
                }
            }
        }
        return map;
    }

    public static ItemState QueryItem(string key)
    {
        var (_, _, _, _, sub, setting) = Find(key);
        var map = QueryAll();
        return map.TryGetValue(setting, out var v) ? new ItemState(v.ac, v.dc) : new ItemState(null, null);
    }

    static (string, string, string, string, string, string) Find(string key)
    {
        foreach (var it in Items)
            if (it.key == key) return it;
        throw new ArgumentException("未知的设置项: " + key);
    }

    static string? RunQuiet(string cmd, params string[] args)
    {
        try { return Run.Exec(cmd, args); }
        catch { return null; }
    }

    /// <summary>设置电源项。mode: both/ac/dc。activate=false 时不立即激活（批量设置后统一调 Activate()）。
    /// 返回 (ok, 错误信息列表)。</summary>
    public static (bool ok, List<string> errors) SetItem(string key, int sec, string mode = "both", bool activate = true)
    {
        var (_, name, _, _, sub, setting) = Find(key);
        mode = (mode ?? "both").ToLowerInvariant();
        var errors = new List<string>();
        if (mode != "dc")
        {
            var (c, t) = Run.ExecFull("powercfg", "/setacvalueindex", "SCHEME_CURRENT", sub, setting, sec.ToString());
            if (c != 0) errors.Add("接通电源: " + ErrText("powercfg", t));
        }
        if (mode != "ac")
        {
            var (c, t) = Run.ExecFull("powercfg", "/setdcvalueindex", "SCHEME_CURRENT", sub, setting, sec.ToString());
            if (c != 0) errors.Add("使用电池: " + ErrText("powercfg", t));
        }
        if (activate && errors.Count == 0)
        {
            var (ok, err) = Activate();
            if (!ok) errors.Add("使设置生效: " + err);
        }
        return (errors.Count == 0, errors);
    }

    /// <summary>使当前电源计划生效（批量设置后调用一次即可）。</summary>
    public static (bool ok, string? error) Activate()
    {
        var g = ActiveScheme().guid;
        var (c, t) = Run.ExecFull("powercfg", "/setactive", g);
        return c == 0 ? (true, null) : (false, ErrText("powercfg", t));
    }

    /// <summary>恢复电源计划系统默认值。</summary>
    public static (bool ok, string? error) RestoreDefaults()
    {
        var (c, t) = Run.ExecFull("powercfg", "-restoredefaultschemes");
        return c == 0 ? (true, null) : (false, ErrText("powercfg", t));
    }

    /// <summary>把子进程输出压成单行错误文本（超出 200 字符截断），便于塞进 MessageBox/CLI 输出。</summary>
    static string ErrText(string what, string output)
    {
        string t = string.IsNullOrWhiteSpace(output) ? what + " 调用失败" : string.Join(" ", output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return t.Length > 200 ? t.Substring(0, 200) + "…" : t;
    }

    /// <summary>解析时间值文本（never/30s/10min/2h/600）→ 秒。</summary>
    public static int ParseValue(string? v)
    {
        string s = (v ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) throw new ArgumentException("缺少时间值");
        if (Regex.IsMatch(s, @"^(never|off|no|false|none|0|永不|从不|关闭|禁用)$")) return 0;
        Match m;
        if ((m = Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(秒|s|sec|secs|second|seconds)$")).Success)
            return (int)Math.Round(double.Parse(m.Groups[1].Value));
        if ((m = Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(分钟|分|m|min|mins|minute|minutes)$")).Success)
            return (int)Math.Round(double.Parse(m.Groups[1].Value) * 60);
        if ((m = Regex.Match(s, @"^(\d+(?:\.\d+)?)\s*(小时|时|h|hr|hrs|hour|hours)$")).Success)
            return (int)Math.Round(double.Parse(m.Groups[1].Value) * 3600);
        if (Regex.IsMatch(s, @"^\d+$")) return int.Parse(s);
        throw new ArgumentException("无法识别的时间值: " + v);
    }
}
