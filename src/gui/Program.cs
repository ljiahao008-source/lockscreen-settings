using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace LockscreenGui;

static class Program
{
    static Mutex? _mutex;

    [STAThread]
    static void Main(string[] args)
    {
        string dbg = Path.Combine(AppContext.BaseDirectory, "boot-log.txt");
        void Log(string m) { try { File.AppendAllText(dbg, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + "\n"); } catch { } }
        Log("main start args=" + string.Join(",", args));
        try { Log("noElevate=" + NoElevateRequested() + " elevated=" + IsElevated()); } catch { }
        // 有参数 = 命令行模式（设完即退，控制台输出；不需要管理员权限）
        if (args.Length > 0)
        {
            Environment.ExitCode = Cli.Run(args);
            return;
        }

        // 默认以管理员模式运行：未提升时通过 UAC 重新启动自己（设置屏保 / 恢复默认 /
        // 开启休眠等操作需要管理员权限）。用户拒绝 UAC 时降级为普通权限继续运行。
        // 单文件程序没有"父进程转交"问题，这里用运行时自提升（而非 manifest requireAdministrator）
        // 是为了：1) 用户拒 UAC 可降级运行；2) 保留 LOCKSCREEN_NO_ELEVATE 测试/逃生开关。
        if (!IsElevated() && !NoElevateRequested())
        {
            if (TryElevate()) return;   // 提升实例已启动，本实例退出（不创建 Mutex，避免互斥冲突）
        }

        // 单实例：重复启动时激活已有窗口并退出新实例
        _mutex = new Mutex(true, @"Local\LockscreenGui.SingleInstance.v3", out bool createdNew);
        if (!createdNew)
        {
            ActivateExisting();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());

        try { _mutex.ReleaseMutex(); } catch { }
    }

    // 当前进程是否已提升（管理员权限）
    static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            return p.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // 环境变量 LOCKSCREEN_NO_ELEVATE=1 时跳过自动提权（自动化/测试场景，或用户希望
    // 保持普通权限运行时使用）
    static bool NoElevateRequested()
    {
        try { return Environment.GetEnvironmentVariable("LOCKSCREEN_NO_ELEVATE") == "1"; }
        catch { return false; }
    }

    // 用 runas 动词请求 UAC 重新启动自身；成功返回 true（新实例负责后续所有工作）
    static bool TryElevate()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            Process.Start(psi);
            return true;
        }
        catch { return false; }   // 用户取消 UAC 或启动失败 → 降级运行
    }

    // 把已运行实例的主窗口恢复到前台（若最小化则先还原）
    static void ActivateExisting()
    {
        try
        {
            string name = Process.GetCurrentProcess().ProcessName;
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(p.MainWindowHandle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
