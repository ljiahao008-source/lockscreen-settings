using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;

namespace LockscreenGui;

/// <summary>
/// 动态锁（Windows Dynamic Lock）：开关/信任设备在 HKCU Winlogon；
/// 蓝牙配对设备与 Keys 权限在 HKLM BTHPORT。
/// Win11 25H2 另有独立的「离开时锁定」开关 LockdownOnLeave（HKCU Authentication\LogonUI），
/// 与动态锁互相独立：只关 EnableGoodbye 它仍会在离开时锁屏，Set(false) 会一并关闭。
/// 全部注册表原生实现，不再调 PowerShell。
/// </summary>
static class DynlockManager
{
    const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string BthDevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
    const string BthKeysKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys";
    // Win11 25H2「离开时锁定」独立开关所在（注意不在 Winlogon 下，在 Authentication\LogonUI 下）
    const string LogonUiKey = @"Software\Microsoft\Windows\CurrentVersion\Authentication\LogonUI";

    public sealed record DynlockState(
        bool Enabled, bool DeviceSelected, int PairedCount, int PhoneCount,
        string? SelectedMac, string? SelectedName, string KeysHealthy, int KeysAce,
        bool LockdownOnLeave);

    static string? ReadStr(string hivePath, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(hivePath);
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    /// <summary>读 DWORD（REG_DWORD 是 int，不能 as string）。</summary>
    static bool ReadDword(string hivePath, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(hivePath);
            var v = k?.GetValue(name);
            if (v is int i) return i == 1;
            if (v is byte[] b && b.Length == 4) return BitConverter.ToInt32(b, 0) == 1;
            string? s = v as string;
            return s == "0x1" || s == "1";
        }
        catch { return false; }
    }

    /// <summary>读 HKLM 设备键的二进制 Name（UTF-8 编码）→ 设备名。</summary>
    static string? GetDeviceName(string mac)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(BthDevicesKey + "\\" + mac);
            var v = k?.GetValue("Name") as byte[];
            if (v == null || v.Length == 0) return null;
            int end = v.Length;
            while (end > 0 && v[end - 1] == 0) end--;
            return Encoding.UTF8.GetString(v, 0, end);
        }
        catch { return null; }
    }

    /// <summary>设备键是否存在于 BTHPORT\Devices（曾配对过即存在；注册表键名不区分大小写）。</summary>
    static bool DeviceKeyExists(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(BthDevicesKey + "\\" + mac.Trim());
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>设备是否为手机类（COD Major Device Class = 2 或名称含手机关键字）。</summary>
    static bool IsPhone(string mac)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(BthDevicesKey + "\\" + mac);
            var cod = k?.GetValue("COD");
            if (cod is byte[] codArr && codArr.Length >= 2)
            {
                int v = BitConverter.ToInt32(new byte[] { codArr[0], codArr[1], 0, 0 }, 0);
                if (((v >> 8) & 0x1f) == 2) return true;
            }
            if (cod is int codInt && ((codInt >> 8) & 0x1f) == 2) return true;
        }
        catch { }
        var name = GetDeviceName(mac) ?? "";
        var n = name.ToLowerInvariant();
        return n.Contains("phone") || n.Contains("mobile") || n.Contains("cell");
    }

    /// <summary>已配对蓝牙设备 MAC 列表（7 天内有 LastSeen，按 Node 版 getPairedDevices 逻辑）。
    /// 注意：LastSeen 是 FILETIME（1601 纪元），必须用 ToFileTimeUtc() 对齐基准，不能直接和 .NET Ticks 比。</summary>
    static List<string> GetPairedDevices()
    {
        var result = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(BthDevicesKey);
            if (root == null) return result;
            long nowFt = DateTime.UtcNow.ToFileTimeUtc();
            long threshold = TimeSpan.FromDays(7).Ticks;
            foreach (var mac in root.GetSubKeyNames())
            {
                try
                {
                    using var dk = root.OpenSubKey(mac);
                    var ls = dk?.GetValue("LastSeen");
                    long ft;
                    if (ls is byte[] b && b.Length >= 8) ft = BitConverter.ToInt64(b, 0);
                    else if (ls is long l) ft = l;
                    else continue;
                    if (nowFt - ft <= threshold) result.Add(mac.ToLowerInvariant());
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    /// <summary>蓝牙 Keys 键 ACL 是否健康（ACE 数 &gt;= 5；被清理软件清空后动态锁会失效）。
    /// 优先用 NTAccount 翻译 ACE；个别 ACE（如孤立 SID）翻译失败会抛异常 → 降级用
    /// SecurityIdentifier 再数一遍，避免健康键被误报成「无法检测」。</summary>
    static (string health, int ace) CheckKeysHealth()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(BthKeysKey);
            if (k == null) return ("unknown", -1);
            var acl = k.GetAccessControl();
            int ace;
            try
            {
                ace = acl.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount)).Count;
            }
            catch
            {
                ace = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count;
            }
            return (ace >= 5 ? "healthy" : "broken", ace);
        }
        catch { return ("unknown", -1); }
    }

    public static DynlockState Get()
    {
        bool enabled = ReadDword(WinlogonKey, "EnableGoodbye");
        string? dp = ReadStr(WinlogonKey, "DevicePairing")?.Trim();
        bool dpSet = !string.IsNullOrWhiteSpace(dp);
        var paired = GetPairedDevices();
        int phoneCount = 0;
        foreach (var m in paired) if (IsPhone(m)) phoneCount++;

        // 信任设备判定：DevicePairing 非空且设备键存在（曾配对）即算已选择。
        // 旧实现还要求设备出现在 7 天 LastSeen 窗口内——手机 8 天没连就被误报
        // 「未勾选信任设备」，但 Windows 动态锁本身仍会用该设备，警告是误导。
        // 设备键打不开（如 CLI 非管理员权限）时退回 7 天窗口判定，行为与旧版一致。
        bool selected;
        if (!dpSet) selected = false;
        else if (DeviceKeyExists(dp!)) selected = true;
        else selected = paired.Contains(dp!.ToLowerInvariant());

        var (health, ace) = CheckKeysHealth();
        string? selectedName = dpSet ? (GetDeviceName(dp!) ?? dp) : null;
        bool lockdown = ReadDword(LogonUiKey, "LockdownOnLeave");
        return new DynlockState(enabled, selected, paired.Count, phoneCount, dp, selectedName, health, ace, lockdown);
    }

    /// <summary>
    /// 开/关动态锁。返回 (ok, warning, error)：warning 是已生效但需要用户知道的事
    /// （如自动补选了信任设备、未检测到配对手机），error 是彻底失败。
    /// </summary>
    public static (bool ok, string? warning, string? error) Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(WinlogonKey);
            if (k == null) return (false, null, "无法打开注册表 Winlogon 键");
            k.SetValue("EnableGoodbye", on ? 1 : 0, RegistryValueKind.DWord);
            string? warning = null;

            if (on)
            {
                // 补回 legacy autoRepairDynamicLock 的关键逻辑：开启时若没有有效信任设备，
                // 自动补选（优先手机类），否则「开关开了但永远不会锁」的静默失效用户完全无感。
                string? dp = ReadStr(WinlogonKey, "DevicePairing")?.Trim();
                if (string.IsNullOrEmpty(dp) || !DeviceKeyExists(dp!))
                {
                    var paired = GetPairedDevices();
                    string? best = null;
                    foreach (var m in paired) if (IsPhone(m)) { best = m; break; }
                    if (best == null && paired.Count > 0) best = paired[0];
                    if (best != null)
                    {
                        k.SetValue("DevicePairing", best, RegistryValueKind.String);
                        warning = "已自动选择信任设备：" + (GetDeviceName(best) ?? best);
                    }
                    else if (string.IsNullOrEmpty(dp))
                    {
                        warning = "已开启，但未检测到已配对的蓝牙设备，请先在系统设置中配对手机";
                    }
                }
            }
            else
            {
                // Win11 25H2「离开时锁定」是独立开关（LockdownOnLeave），只关 EnableGoodbye
                // 它照样锁屏。用户关动态锁的意图是不再离开自动锁 → 一并关掉。
                // 仅在该值已存在且非 0 时写 0，不新增注册表值（保持系统最小改动）。
                TryDisableLockdownOnLeave();
            }
            return (true, warning, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    static void TryDisableLockdownOnLeave()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LogonUiKey, writable: true);
            if (k == null) return;
            var v = k.GetValue("LockdownOnLeave");
            if (v is int i && i != 0)
                k.SetValue("LockdownOnLeave", 0, RegistryValueKind.DWord);
        }
        catch { /* 无权限或键不存在：不影响 EnableGoodbye 主流程 */ }
    }

    /// <summary>打开系统设置页（动态锁 / 登录选项 / 蓝牙）。</summary>
    public static bool OpenSettings(string target)
    {
        string uri = target switch
        {
            "signin" => "ms-settings:signinoptions",
            "bluetooth" => "ms-settings:bluetooth",
            _ => "ms-settings:signinoptions-dynamiclock"
        };
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
