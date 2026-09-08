using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LockscreenGui;

/// <summary>
/// 屏幕保护程序：HKCU\Control Panel\Desktop 注册表 + SystemParametersInfo 广播立即生效。
/// </summary>
static class SaverManager
{
    const string DesktopKey = @"Control Panel\Desktop";

    public sealed record SaverState(bool Active, int Timeout, bool Secure)
    {
        public string Text => Active ? (Timeout > 0 ? PowerManager.FormatSeconds(Timeout) : Timeout + " 秒") : "已关闭";
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(int uAction, int uParam, int lpvParam, int fuWinIni);

    public static SaverState Get()
    {
        using var k = Registry.CurrentUser.OpenSubKey(DesktopKey);
        string active = k?.GetValue("ScreenSaveActive") as string ?? "0";
        string timeout = k?.GetValue("ScreenSaveTimeOut") as string ?? "900";
        string secure = k?.GetValue("ScreenSaverIsSecure") as string ?? "0";
        bool on = active == "1";
        int t = 0; int.TryParse(timeout, out t);
        return new SaverState(on, t, secure == "1");
    }

    public static (bool ok, List<string> errors) Set(bool on, int? timeout, bool? secure)
    {
        var errors = new List<string>();
        using var k = Registry.CurrentUser.CreateSubKey(DesktopKey);
        if (k == null) { errors.Add("无法打开注册表 " + DesktopKey); return (false, errors); }
        try
        {
            k.SetValue("ScreenSaveActive", on ? "1" : "0");
            if (timeout != null) k.SetValue("ScreenSaveTimeOut", timeout.Value.ToString());
            if (secure != null) k.SetValue("ScreenSaverIsSecure", secure.Value ? "1" : "0");
        }
        catch (Exception ex) { errors.Add("写入注册表失败: " + ex.Message); }

        // 通知系统立即生效（SPI_SETSCREENSAVETIMEOUT=0x000F, SPI_SETSCREENSAVEACTIVE=0x0011,
        // SPIF_UPDATEINIFILE|SPIF_SENDWININICHANGE=3）
        // 注意：timeout 为 null（如 CLI saver=off）时绝不能调 SPI_SETSCREENSAVETIMEOUT——
        // 传 0 会把 ScreenSaveTimeOut 注册表值覆盖成 0，用户原有的屏保超时设置被清掉。
        try
        {
            if (timeout != null) SystemParametersInfo(0x000F, timeout.Value, 0, 3);
            SystemParametersInfo(0x0011, on ? 1 : 0, 0, 3);
        }
        catch { /* 忽略，注册表已写入 */ }
        return (errors.Count == 0, errors);
    }
}
