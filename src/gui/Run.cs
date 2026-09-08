using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LockscreenGui;

/// <summary>
/// 子进程执行 + GBK 解码工具（替代原 Node 后端的 execFileSync + TextDecoder('gbk')）。
/// 零 NuGet 依赖：GBK 解码走 kernel32 MultiByteToWideChar（代码页 936）。
/// </summary>
static class Run
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int MultiByteToWideChar(uint codePage, uint dwFlags,
        byte[] lpMultiByteStr, int cbMultiByte, char[]? lpWideCharStr, int cchWideChar);

    /// <summary>把 GBK 字节解码为字符串。</summary>
    public static string DecodeGBK(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        int len = MultiByteToWideChar(936, 0, data, data.Length, null, 0);
        if (len <= 0) return Encoding.UTF8.GetString(data);
        var chars = new char[len];
        MultiByteToWideChar(936, 0, data, data.Length, chars, len);
        return new string(chars);
    }

    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>执行命令并返回解码后的文本；进程无法启动返回 null（不区分退出码，只表示"有没有跑起来"）。</summary>
    public static string? Exec(string fileName, params string[] args)
    {
        var (code, text) = ExecFull(fileName, args);
        return code == int.MinValue ? null : text;
    }

    /// <summary>执行命令并返回 (退出码, 合并解码文本)。进程无法启动时退出码为 int.MinValue。</summary>
    public static (int code, string text) ExecFull(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = null
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (int.MinValue, "");
            var tOut = Task.Run(() => ReadAll(p.StandardOutput.BaseStream));
            var tErr = Task.Run(() => ReadAll(p.StandardError.BaseStream));
            p.WaitForExit();
            byte[] stdout = tOut.Result, stderr = tErr.Result;
            string sOut = DecodeSmart(stdout);
            string sErr = DecodeSmart(stderr);
            return (p.ExitCode, (sOut + sErr).TrimEnd('\r', '\n'));
        }
        catch { return (int.MinValue, ""); }
    }

    /// <summary>智能解码：先按 UTF-8（含 BOM）尝试，非法序列再按 GBK（powercfg/reg 是 GBK，PowerShell 是 UTF-8）。</summary>
    static string DecodeSmart(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return utf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return DecodeGBK(data);
        }
    }
}
