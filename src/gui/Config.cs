using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace LockscreenGui;

/// <summary>
/// 配置导出 / 导入（换电脑 / 重装系统迁移）。JSON 格式与原 Node 版兼容。
/// </summary>
static class ConfigIO
{
    public const string DefaultFile = "锁屏设置.json";

    public static string AppDir
    {
        get
        {
            // 单文件发布：exe 所在目录（便携：数据文件与 exe 同目录）
            string? p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? AppContext.BaseDirectory : Path.GetDirectoryName(p) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary>配置默认存放目录：data 子目录（保持根目录只有程序本体）。</summary>
    public static string DataDir
    {
        get
        {
            string d = Path.Combine(AppDir, "data");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>相对路径 / 纯文件名落到 data 子目录。</summary>
    public static string ResolvePath(string? file)
    {
        if (string.IsNullOrEmpty(file)) return Path.Combine(DataDir, DefaultFile);
        if (Path.IsPathRooted(file)) return file;
        if (file.Contains('\\') || file.Contains('/')) return Path.GetFullPath(file);
        return Path.Combine(DataDir, file);
    }

    public static JsonObject Build()
    {
        var map = PowerManager.QueryAll();
        var items = new JsonObject();
        foreach (var k in PowerManager.Keys)
        {
            var (_, _, _, _, _, setting) = Find(k);
            (int? ac, int? dc) v = (null, null);
            if (map.TryGetValue(setting, out var x)) v = x;
            items[k] = new JsonObject
            {
                // 读不到（如休眠未开启时 HIBERNATEIDLE 不在输出里）导出 null 而非 0：
                // 0 会被当成「永不」，换机导入后把目标机对应设置意外清掉。
                // 导入端 TryInt(null) 返回 null 会跳过该项，保持目标机原值。
                ["ac"] = v.ac,
                ["dc"] = v.dc
            };
        }
        var sv = SaverManager.Get();
        var dl = DynlockManager.Get();
        var scheme = PowerManager.ActiveScheme();
        return new JsonObject
        {
            ["app"] = "lockscreen-delay",
            ["type"] = "lockscreen-delay-config",
            ["version"] = "3.1.0",
            ["exportedAt"] = DateTime.Now.ToString("o"),
            ["computer"] = Environment.MachineName,
            ["scheme"] = new JsonObject { ["name"] = scheme.name, ["guid"] = scheme.guid },
            ["items"] = items,
            ["saver"] = new JsonObject
            {
                ["active"] = sv.Active,
                ["timeout"] = sv.Timeout,
                ["secure"] = sv.Secure
            },
            // v3.1 起导出动态锁开关，换机迁移与电源/屏保对齐。
            // 只导出开关不导出信任设备：DevicePairing 与具体机器的蓝牙配对绑定，
            // 换机后 MAC 大概率对不上，导入端 Set(true) 会自动补选目标机的配对手机。
            ["dynlock"] = new JsonObject
            {
                ["enabled"] = dl.Enabled
            }
        };
    }

    static (string, string, string, string, string, string) Find(string key)
    {
        foreach (var it in PowerManager.Items)
            if (it.key == key) return it;
        throw new ArgumentException("未知的设置项: " + key);
    }

    public static (string file, JsonObject config) Read(string? file)
    {
        string p = ResolvePath(file);
        if (!File.Exists(p)) throw new FileNotFoundException("找不到配置文件：" + p + "\n  （不带文件名时默认找 data 子目录下的 " + DefaultFile + "）");
        JsonNode? j;
        try { j = JsonNode.Parse(File.ReadAllText(p)); }
        catch (Exception ex) { throw new FormatException("配置文件不是有效的 JSON：" + ex.Message); }
        if (j is not JsonObject jo || jo["items"] == null)
            throw new FormatException("这不像是本工具导出的配置文件（缺少 items 字段）");
        return (p, jo);
    }

    public static string Export(string? file)
    {
        string p = ResolvePath(file);
        // UnsafeRelaxedJsonEscaping：默认 Encoder 会把中文/符号全部转义成 \uXXXX，
        // 导出文件是给人看的（--show / 记事本），保留可读性更重要
        File.WriteAllText(p, Build().ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return p;
    }

    public static (bool ok, List<string> results, List<string> errors) Apply(JsonObject cfg)
    {
        var results = new List<string>();
        var errors = new List<string>();
        var items = cfg["items"] as JsonObject;
        int applied = 0;
        if (items != null)
        {
            foreach (var k in PowerManager.Keys)
            {
                var node = items[k];
                if (node == null) continue;
                var (key, name, _, _, _, _) = Find(k);
                int? ac = TryInt(node["ac"]), dc = TryInt(node["dc"]);
                if (ac == null || dc == null) continue;
                applied++;
                // 先批量写（activate=false），最后统一激活一次，避免导入 8 项时反复 /setactive
                var (ok, errs) = PowerManager.SetItem(k, ac.Value, "ac", false);
                var (ok2, errs2) = PowerManager.SetItem(k, dc.Value, "dc", false);
                if (!ok) errors.Add(name + "：接通电源 " + string.Join("；", errs));
                if (!ok2) errors.Add(name + "：使用电池 " + string.Join("；", errs2));
                results.Add("  " + name + "  接通电源 " + PowerManager.FormatSeconds(ac.Value) + "  使用电池 " + PowerManager.FormatSeconds(dc.Value));
            }
            var (actOk, actErr) = PowerManager.Activate();
            if (!actOk) errors.Add("使设置生效：" + (actErr ?? "powercfg 调用失败"));
        }
        var saver = cfg["saver"] as JsonObject;
        if (saver != null)
        {
            bool on = saver["active"]?.GetValue<bool>() ?? false;
            int? t = TryInt(saver["timeout"]);
            bool? sec = saver["secure"]?.GetValue<bool>();
            var (ok, errs) = SaverManager.Set(on, t, sec);
            if (!ok) errors.Add("屏幕保护程序：" + string.Join("；", errs));
            results.Add("  屏幕保护程序  " + (on ? PowerManager.FormatSeconds(t ?? 0) : "已关闭"));
        }
        // v3.1 起支持动态锁段；旧配置文件没有该段则跳过（向后兼容）
        if (cfg["dynlock"] is JsonObject dl && TryBool(dl["enabled"]) is bool dlOn)
        {
            var (ok, warning, err) = DynlockManager.Set(dlOn);
            if (!ok) errors.Add("动态锁：" + (err ?? "未知错误"));
            else results.Add("  动态锁  " + (dlOn ? "已开启" + (warning != null ? "（" + warning + "）" : "") : "已关闭（含 Win11「离开时锁定」独立开关）"));
        }
        if (applied == 0 && saver == null && cfg["dynlock"] == null) errors.Add("配置里没有可用的设置项（items 为空）");
        return (errors.Count == 0, results, errors);
    }

    static bool? TryBool(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<bool>(out bool b)) return b;
        if (n is JsonValue v2 && v2.TryGetValue<string>(out string? s))
        {
            s = s.Trim().ToLowerInvariant();
            if (s is "true" or "1" or "on" or "开" or "开启") return true;
            if (s is "false" or "0" or "off" or "关" or "关闭") return false;
        }
        if (n is JsonValue v3 && v3.TryGetValue<int>(out int i)) return i != 0;
        return null;
    }

    static int? TryInt(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<int>(out int i)) return i;
        if (n is JsonValue v2 && v2.TryGetValue<long>(out long l)) return (int)l;
        if (n is JsonValue v3 && v3.TryGetValue<string>(out string? s) && int.TryParse(s, out int i3)) return i3;
        return null;
    }
}
