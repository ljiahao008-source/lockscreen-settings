using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LockscreenGui;

/// <summary>
/// 命令行模式（保留原 Node 版的 CLI 能力，去掉服务相关参数）：
///   --status / --list            打印当前所有设置
///   --export[=file]              导出配置（默认 锁屏设置.json，存 exe 同目录）
///   --import[=file]              导入配置
///   --show[=file]                只查看配置内容，不应用
///   key=value                    直接设置（如 display=10min，支持 ac:/dc: 前缀）
///   saver=on|off|time            屏幕保护
///   dynlock=on|off|status        动态锁
///   --help                       帮助
/// 无参数时启动图形界面（另走 Program.Main 的 GUI 分支）。
/// </summary>
static class Cli
{
    static readonly (string key, string name)[] Items =
    {
        ("lock", "锁屏后黑屏"), ("display", "关闭显示器"), ("sleep", "睡眠"), ("hibernate", "休眠")
    };

    public static int Run(string[] args)
    {
        // GUI 子系统下可能没有控制台句柄（如被某些宿主直接拉起），设置编码失败就跳过
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            var parsed = Parse(args);
            if (parsed == null) return 0;   // 已打印帮助

            if (parsed.Status) { PrintStatus(); return 0; }
            if (parsed.ShowFile != null) { ShowFile(parsed.ShowFile); return 0; }
            if (parsed.ImportFile != null) return Import(parsed.ImportFile);
            if (parsed.ExportFile != null) { Export(parsed.ExportFile); return 0; }
            if (parsed.Sets.Count > 0) return ApplySets(parsed.Sets);
            if (parsed.DynlockAction != null) return DynlockCli(parsed.DynlockAction);

            Console.WriteLine("无可执行的操作。用 --help 查看用法。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("\n  [错误] " + ex.Message + "\n");
            return 1;
        }
    }

    sealed class Parsed
    {
        public bool Status;
        public string? ExportFile;
        public string? ImportFile;
        public string? ShowFile;
        public List<(string mode, string key, string raw)> Sets = new();
        public string? DynlockAction;
    }

    static string? Alias(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "lock" or "lockscreen" or "screensaverlock" or "锁屏" or "锁屏后黑屏" => "lock",
            "display" or "screen" or "monitor" or "off" or "显示器" or "屏幕" or "关闭显示器" => "display",
            "sleep" or "standby" or "睡眠" => "sleep",
            "hibernate" or "hib" or "休眠" => "hibernate",
            "saver" or "screensaver" or "屏保" or "屏幕保护" => "saver",
            // 缺陷修复：此前 dynlock 没在别名表里，文档承诺的 dynlock=on/off/status 会直接抛
            // 「未知的设置项」，ApplySets 里的 dynlock 分支是永远走不到的死代码
            "dynlock" or "dynamiclock" or "dynamic-lock" or "动态锁" => "dynlock",
            _ => null
        };
    }

    static Parsed? Parse(string[] args)
    {
        var p = new Parsed();
        foreach (var a in args)
        {
            if (a is "--help" or "-h" or "help") { PrintHelp(); return null; }
            if (a is "--status" or "--list" or "-l") { p.Status = true; continue; }
            if (a.StartsWith("--export", StringComparison.OrdinalIgnoreCase))
                { p.ExportFile = a.Contains('=') ? a.Substring(a.IndexOf('=') + 1) : ""; continue; }
            if (a.StartsWith("--import", StringComparison.OrdinalIgnoreCase))
                { p.ImportFile = a.Contains('=') ? a.Substring(a.IndexOf('=') + 1) : ""; continue; }
            if (a.StartsWith("--show", StringComparison.OrdinalIgnoreCase))
                { p.ShowFile = a.Contains('=') ? a.Substring(a.IndexOf('=') + 1) : ""; continue; }
            if (a.StartsWith("--dynlock", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Contains('=') ? a.Substring(a.IndexOf('=') + 1).Trim().ToLowerInvariant() : "status";
                p.DynlockAction = v switch
                {
                    "on" or "1" or "true" or "开" or "开启" => "on",
                    "off" or "0" or "false" or "关" or "关闭" => "off",
                    _ => "status"
                };
                continue;
            }
            if (a.StartsWith("--"))
                throw new ArgumentException("无法识别的参数：" + a + "\n使用 --help 查看用法");

            // key=value / ac:key=value / dc:key=value
            var m = System.Text.RegularExpressions.Regex.Match(a, @"^(?:(ac|dc|both)[:：])?([A-Za-z\u4e00-\u9fa5]+)\s*[=:]\s*(.+)$");
            if (!m.Success)
                throw new ArgumentException("无法识别的参数：" + a + "\n使用 --help 查看用法");
            string? key = Alias(m.Groups[2].Value);
            if (key == null)
                throw new ArgumentException("未知的设置项：" + m.Groups[2].Value);
            p.Sets.Add((m.Groups[1].Value.ToLowerInvariant(), key, m.Groups[3].Value.Trim()));
        }
        return p;
    }

    // 返回 0/1：失败的设置项会让 exit code = 1（供脚本 %errorlevel% 判断）。
    // 注意不能只在函数内改 Environment.ExitCode——Program.Main 会用 Cli.Run 的返回值覆盖它。
    static int ApplySets(List<(string mode, string key, string raw)> sets)
    {
        Console.WriteLine();
        int code = 0;
        foreach (var s in sets)
        {
            try
            {
                if (s.key == "saver")
                {
                    string v = s.raw.ToLowerInvariant();
                    bool on;
                    int? timeout = null;
                    if (v is "on" or "1" or "true" or "开" or "开启") on = true;
                    else if (v is "off" or "0" or "false" or "关" or "关闭") on = false;   // 0 也归 off，避免被 ParseValue 当成"永不"而意外开启
                    else { on = true; timeout = PowerManager.ParseValue(s.raw); }
                    var (ok, errs) = SaverManager.Set(on, timeout, null);
                    Console.WriteLine("  屏幕保护程序 -> " + (on ? (timeout != null ? PowerManager.FormatSeconds(timeout.Value) : "开启") : "关闭") + (ok ? "" : "   [失败] " + string.Join("；", errs)));
                    if (!ok) code = 1;
                }
                else if (s.key == "dynlock")
                {
                    string v = s.raw.ToLowerInvariant();
                    if (v is "status" or "show" or "state" or "查" or "看") { PrintDynlock(); }
                    else
                    {
                        bool on = v is "on" or "1" or "true" or "开" or "开启";
                        var r = DynlockManager.Set(on);
                        Console.WriteLine("  动态锁已" + (on ? "开启" : "关闭") + (r.ok ? "" : "（失败：" + r.error + "）"));
                        if (r.ok && r.warning != null) Console.WriteLine("  [提示] " + r.warning);
                        if (!r.ok) code = 1;
                        PrintDynlock();
                    }
                }
                else
                {
                    int sec = PowerManager.ParseValue(s.raw);
                    var (ok, errs) = PowerManager.SetItem(s.key, sec, s.mode);
                    string tag = (s.mode is "" or "both") ? "" : "（" + (s.mode == "ac" ? "仅接通电源" : "仅使用电池") + "）";
                    Console.WriteLine("  " + NameOf(s.key) + tag + " -> " + PowerManager.FormatSeconds(sec) + (ok ? "" : "   [失败] " + string.Join("；", errs)));
                    if (!ok) code = 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  " + s.key + " -> [错误] " + ex.Message);
                code = 1;
            }
        }
        Console.WriteLine();
        return code;
    }

    static string NameOf(string key)
    {
        foreach (var it in Items) if (it.key == key) return it.name;
        return key;
    }

    static void PrintStatus()
    {
        var scheme = PowerManager.ActiveScheme();
        var map = PowerManager.QueryAll();
        bool battery = PowerManager.HasBattery();
        Console.WriteLine();
        Console.WriteLine("  当前电源计划：" + scheme.name + "   (" + scheme.guid + ")");
        Console.WriteLine("  休眠功能：" + (PowerManager.HibernateAvailable() ? "已开启" : "未开启（休眠设置不会生效）"));
        Console.WriteLine();
        if (battery) Console.WriteLine("  " + "项目".PadRight(18) + "接通电源".PadRight(14) + "使用电池");
        else Console.WriteLine("  " + "项目".PadRight(18) + "当前值");
        foreach (var (key, name) in Items)
        {
            string setting = key switch { "lock" => "VIDEOCONLOCK", "display" => "VIDEOIDLE", "sleep" => "STANDBYIDLE", _ => "HIBERNATEIDLE" };
            (int? ac, int? dc) v = (null, null);
            if (map.TryGetValue(setting, out var x)) v = x;
            if (battery)
                Console.WriteLine("  " + name.PadRight(18) + PowerManager.FormatSeconds(v.ac ?? 0).PadRight(14) + PowerManager.FormatSeconds(v.dc ?? 0));
            else
                Console.WriteLine("  " + name.PadRight(18) + PowerManager.FormatSeconds(v.ac ?? 0));
        }
        var sv = SaverManager.Get();
        Console.WriteLine("  " + "屏幕保护".PadRight(18) + (sv.Active ? sv.Timeout + " 秒" : "已关闭"));
        PrintDynlock();
        Console.WriteLine();
    }

    static void PrintDynlock()
    {
        var dl = DynlockManager.Get();
        string device = dl.SelectedName ?? dl.SelectedMac ?? "(未配置)";
        Console.WriteLine("  " + "动态锁".PadRight(18) + (dl.Enabled ? "已开启" : "已关闭") +
            "  信任设备 " + (dl.DeviceSelected ? device : "未选择") +
            "  蓝牙密钥" + (dl.KeysHealthy == "healthy" ? "正常" : dl.KeysHealthy == "broken" ? "异常" : "无法检测") +
            (dl.LockdownOnLeave ? "  [!] Win11「离开时锁定」独立开关仍开启（关闭动态锁时会一并关闭）" : ""));
    }

    static void Export(string? file)
    {
        string p = ConfigIO.Export(file);
        Console.WriteLine("\n  配置已导出到：" + p);
        Console.WriteLine("  换电脑时把这个文件和 exe 一起拷过去，在新电脑上运行：");
        Console.WriteLine("    锁屏设置.exe --import=" + Path.GetFileName(p));
        Console.WriteLine();
    }

    static int Import(string? file)
    {
        var (p, cfg) = ConfigIO.Read(file);
        Console.WriteLine("\n  已读取：" + p);
        var (ok, results, errors) = ConfigIO.Apply(cfg);
        Console.WriteLine("  还原内容：");
        foreach (var r in results) Console.WriteLine(r);
        if (!ok) Console.WriteLine("\n  [部分失败] " + string.Join("；", errors));
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    static void ShowFile(string? file)
    {
        var (p, cfg) = ConfigIO.Read(file);
        Console.WriteLine("\n  配置文件：" + p);
        var exp = cfg["exportedAt"]?.GetValue<string>();
        if (exp != null) Console.WriteLine("  导出时间：" + exp.Replace('T', ' ').Substring(0, Math.Min(19, exp.Length)));
        var scheme = cfg["scheme"] as System.Text.Json.Nodes.JsonObject;
        if (scheme != null && scheme["name"] != null) Console.WriteLine("  原电源计划：" + scheme["name"]!.GetValue<string>());
        Console.WriteLine();
        var items = cfg["items"] as System.Text.Json.Nodes.JsonObject;
        if (items != null)
        {
            foreach (var (key, name) in Items)
            {
                var v = items[key] as System.Text.Json.Nodes.JsonObject;
                if (v == null) continue;
                int? ac = GetInt(v["ac"]), dc = GetInt(v["dc"]);
                Console.WriteLine("  " + name.PadRight(16) + "接通电源 " + PowerManager.FormatSeconds(ac ?? 0) + "  使用电池 " + PowerManager.FormatSeconds(dc ?? 0));
            }
        }
        var sv = cfg["saver"] as System.Text.Json.Nodes.JsonObject;
        if (sv != null && sv["active"] != null)
            Console.WriteLine("  " + "屏幕保护程序".PadRight(16) + (sv["active"]!.GetValue<bool>() ? PowerManager.FormatSeconds(GetInt(sv["timeout"]) ?? 0) : "已关闭"));
        Console.WriteLine();
    }

    static int? GetInt(System.Text.Json.Nodes.JsonNode? n)
    {
        if (n is System.Text.Json.Nodes.JsonValue v)
        {
            if (v.TryGetValue<int>(out int i)) return i;
            if (v.TryGetValue<long>(out long l)) return (int)l;
            if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int i2)) return i2;
        }
        return null;
    }

    static int DynlockCli(string action)
    {
        if (action == "status") { PrintDynlock(); return 0; }
        bool on = action == "on";
        var r = DynlockManager.Set(on);
        Console.WriteLine("\n  动态锁已" + (on ? "开启" : "关闭") + (r.ok ? "" : "（失败：" + r.error + "）"));
        if (r.ok && r.warning != null) Console.WriteLine("  [提示] " + r.warning);
        PrintDynlock();
        Console.WriteLine();
        return r.ok ? 0 : 1;
    }

    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  锁屏设置  v3.1（单文件 C# 版）");
        Console.WriteLine();
        Console.WriteLine("  用法：");
        Console.WriteLine("    锁屏设置.exe                    启动图形界面");
        Console.WriteLine("    锁屏设置.exe display=10min      直接设置（设完即退）");
        Console.WriteLine("    锁屏设置.exe --status           查看当前所有设置");
        Console.WriteLine();
        Console.WriteLine("  设置项：lock(锁屏后黑屏) / display(关闭显示器) / sleep(睡眠) / hibernate(休眠)");
        Console.WriteLine("    saver    屏幕保护程序（on / off / 时间）");
        Console.WriteLine("    dynlock  动态锁（on / off / status）");
        Console.WriteLine("  时间写法：never(永不) / 30s / 10min / 2h / 600（纯数字=秒）");
        Console.WriteLine("  前缀 ac: / dc: 可只改「接通电源」或「使用电池」，例如 ac:sleep=30min");
        Console.WriteLine();
        Console.WriteLine("  其它参数：--export[=文件] / --import[=文件] / --show[=文件] / --help");
        Console.WriteLine("  导出 / 导入不需要管理员权限，适合换电脑或重装系统后恢复。");
        Console.WriteLine();
    }
}
