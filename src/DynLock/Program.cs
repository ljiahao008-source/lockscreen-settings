using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace DynLockStandalone;

/// <summary>
/// DynLockStandalone - 与 Windows 动态锁对齐思路的独立实现。
/// 通过监听指定手机/设备的 BLE 广播 RSSI，在信号持续低于阈值或设备消失后锁屏。
/// </summary>
class Program
{
    // ---- Win32 ----
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    // ---- Paths ----
    static readonly string AppDir = AppContext.BaseDirectory;
    static readonly string DefaultConfigPath = Path.Combine(AppDir, "dynlock.json");
    static readonly string DefaultLogPath = Path.Combine(AppDir, "dynlock.log");

    // ---- State ----
    static DynLockConfig config = new();
    static string configPath = DefaultConfigPath;
    static StreamWriter? logWriter;
    static readonly object stateLock = new();

    static DateTimeOffset lastSeen = DateTimeOffset.MinValue;
    static double smoothedRssi = -128;
    static readonly Queue<double> rssiWindow = new();
    static bool isInRange = true;
    static DateTimeOffset? outOfRangeSince;
    static bool lockTriggered;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += OnCancel;
        AppDomain.CurrentDomain.ProcessExit += OnExit;

        var parsed = ParseArgs(args);
        if (parsed.Help)
        {
            PrintHelp();
            return 0;
        }

        configPath = parsed.ConfigPath ?? DefaultConfigPath;
        LoadConfig();
        InitLogger();

        Log("=================================================");
        Log("  DynLockStandalone v" + GetVersion());
        Log("  独立动态锁 - BLE RSSI 联动锁屏");
        Log("=================================================");
        Log($"配置文件: {configPath}");

        if (parsed.Scan)
        {
            return RunDiscovery(parsed.ScanDurationMs).GetAwaiter().GetResult();
        }

        return RunMonitor();
    }

    static int RunMonitor()
    {
        if (string.IsNullOrWhiteSpace(config.DeviceMac) ||
            !TryParseMacToBluetoothAddress(config.DeviceMac, out ulong targetMac))
        {
            Log("错误: dynlock.json 中 DeviceMac 为空或格式不正确。", isError: true);
            Log("请先用 --scan 查找设备 MAC，再填入配置文件。", isError: true);
            Log($"默认配置文件路径: {configPath}", isError: true);
            return 1;
        }

        Log($"目标设备: {config.DeviceMac} ({config.DeviceName ?? "未命名"})");
        Log($"锁屏阈值: {config.LockThresholdDbm} dBm");
        Log($"丢失超时: {config.OutOfRangeTimeoutMs} ms");
        Log($"锁屏延迟: {config.LockDelayMs} ms");
        Log($"扫描周期: {config.ScanIntervalMs} ms");
        Log($"RSSI 窗口: {config.RssiWindowSize}, 平滑系数: {config.RssiSmoothing:F2}");
        Log("按 Ctrl+C 退出。");

        using var cts = new CancellationTokenSource();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = config.ActiveScan ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive
        };

        watcher.SignalStrengthFilter.InRangeThresholdInDBm = (short)config.LockThresholdDbm;
        watcher.SignalStrengthFilter.OutOfRangeThresholdInDBm = (short)(config.LockThresholdDbm - 5);
        watcher.SignalStrengthFilter.OutOfRangeTimeout = TimeSpan.FromMilliseconds(config.OutOfRangeTimeoutMs);

        watcher.Received += (s, e) =>
        {
            if (e.BluetoothAddress != targetMac) return;
            lock (stateLock)
            {
                lastSeen = DateTimeOffset.Now;
                UpdateRssi(e.RawSignalStrengthInDBm);
            }
        };

        watcher.Stopped += (s, e) =>
        {
            if (e.Error != BluetoothError.Success)
            {
                Log($"BLE 扫描器停止，错误: {e.Error}", isError: true);
            }
        };

        watcher.Start();
        Log("BLE 扫描已启动，等待设备广播...");

        using var timer = new Timer(_ => Evaluate(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(config.ScanIntervalMs));

        try
        {
            // 阻塞主线程，直到收到取消信号
            Task.Delay(Timeout.Infinite, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        watcher.Stop();
        Log("DynLockStandalone 已停止。");
        return 0;
    }

    static async Task<int> RunDiscovery(int durationMs)
    {
        Log($"开始扫描 BLE 设备，持续 {durationMs / 1000} 秒...");
        var seen = new Dictionary<ulong, (string name, List<short> rssiValues)>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (s, e) =>
        {
            var mac = BluetoothAddressToMacString(e.BluetoothAddress);
            var name = e.Advertisement.LocalName ?? "";
            if (!seen.TryGetValue(e.BluetoothAddress, out var entry))
            {
                entry = (name, new List<short>());
                seen[e.BluetoothAddress] = entry;
            }
            if (!string.IsNullOrWhiteSpace(name)) entry.name = name;
            entry.rssiValues.Add(e.RawSignalStrengthInDBm);
        };

        watcher.Start();
        await Task.Delay(durationMs);
        watcher.Stop();

        Log("扫描结束。发现的设备：");
        if (seen.Count == 0)
        {
            Log("  (未扫描到任何 BLE 设备)");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{"MAC",-18} {"名称",-25} {"平均 RSSI",12} {"样本数",8}");
        Console.WriteLine(new string('-', 70));
        foreach (var kv in seen.OrderByDescending(x => x.Value.rssiValues.Average(v => (double)v)))
        {
            var avg = kv.Value.rssiValues.Average(v => (double)v);
            Console.WriteLine($"{BluetoothAddressToMacString(kv.Key),-18} {kv.Value.name ?? "",-25} {avg,12:F1} dBm {kv.Value.rssiValues.Count,8}");
        }
        Console.WriteLine();
        Log("提示: 将目标设备的 MAC 填入 dynlock.json 的 DeviceMac 字段即可使用。");
        return 0;
    }

    static void UpdateRssi(short rssi)
    {
        rssiWindow.Enqueue(rssi);
        while (rssiWindow.Count > config.RssiWindowSize) rssiWindow.Dequeue();

        var sorted = rssiWindow.OrderBy(x => x).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        // 指数平滑：系数越大历史权重越高
        if (smoothedRssi <= -100) smoothedRssi = median;
        else smoothedRssi = config.RssiSmoothing * smoothedRssi + (1 - config.RssiSmoothing) * median;
    }

    static void Evaluate()
    {
        lock (stateLock)
        {
            var now = DateTimeOffset.Now;
            bool present = lastSeen != DateTimeOffset.MinValue &&
                           (now - lastSeen).TotalMilliseconds <= config.OutOfRangeTimeoutMs;
            bool strongEnough = smoothedRssi >= config.LockThresholdDbm;
            bool currentlyInRange = present && strongEnough;

            if (currentlyInRange)
            {
                if (!isInRange)
                {
                    Log($"设备回到范围: RSSI={smoothedRssi:F1} dBm");
                    lockTriggered = false;
                }
                outOfRangeSince = null;
                isInRange = true;
            }
            else
            {
                if (isInRange)
                {
                    string reason = present
                        ? $"信号弱 (RSSI={smoothedRssi:F1} dBm < {config.LockThresholdDbm} dBm)"
                        : "未收到广播";
                    Log($"设备离开范围: {reason}");
                    outOfRangeSince = now;
                    isInRange = false;
                }

                if (outOfRangeSince.HasValue && !lockTriggered)
                {
                    var elapsed = (now - outOfRangeSince.Value).TotalMilliseconds;
                    if (elapsed >= config.LockDelayMs)
                    {
                        TriggerLock();
                    }
                }
            }
        }
    }

    static void TriggerLock()
    {
        Log($"触发锁屏 (RSSI={smoothedRssi:F1} dBm)");
        if (LockWorkStation())
        {
            Log("锁屏命令已发送 (LockWorkStation)。");
            lockTriggered = true;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Log($"锁屏失败，Win32 错误码: {err}", isError: true);
        }
    }

    // ---- Config ----

    static void LoadConfig()
    {
        if (!File.Exists(configPath))
        {
            config = new DynLockConfig();
            SaveConfig();
            return;
        }
        try
        {
            var json = File.ReadAllText(configPath);
            config = JsonSerializer.Deserialize(json, DynLockConfigContext.Default.DynLockConfig) ?? new DynLockConfig();
        }
        catch (Exception ex)
        {
            Log($"读取配置失败: {ex.Message}", isError: true);
            config = new DynLockConfig();
        }
    }

    static void SaveConfig()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = DynLockConfigContext.Default };
            var json = JsonSerializer.Serialize(config, typeof(DynLockConfig), options);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            Log($"保存默认配置失败: {ex.Message}", isError: true);
        }
    }

    // ---- Logging ----

    static void InitLogger()
    {
        try
        {
            var logPath = string.IsNullOrWhiteSpace(config.LogPath) ? DefaultLogPath : config.LogPath;
            logWriter = new StreamWriter(logPath, append: true, encoding: Encoding.UTF8) { AutoFlush = true };
        }
        catch { }
    }

    static void Log(string message, bool isError = false)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        if (isError) line = "[ERROR] " + line;
        Console.WriteLine(line);
        logWriter?.WriteLine(line);
    }

    // ---- MAC helpers ----

    /// <summary>
    /// 将 "AA:BB:CC:DD:EE:FF" 或 "AABBCCDDEEFF" 解析为 WinRT 内部使用的 ulong BluetoothAddress。
    /// WinRT 以 little-endian 字节顺序存储 MAC，因此需要按字节反转。
    /// </summary>
    static bool TryParseMacToBluetoothAddress(string mac, out ulong value)
    {
        value = 0;
        var hex = new string(mac.Where(c => c != ':' && c != '-' && c != ' ').ToArray()).ToLowerInvariant();
        if (hex.Length != 12 || !hex.All("0123456789abcdef".Contains))
            return false;

        var macBytes = Enumerable.Range(0, 12)
            .Where(i => i % 2 == 0)
            .Select(i => Convert.ToByte(hex.Substring(i, 2), 16))
            .ToArray();

        // WinRT BluetoothAddress 的最低有效字节 = MAC 字符串最后一个字节
        var addressBytes = macBytes.Reverse().Concat(new byte[2]).ToArray();
        value = BitConverter.ToUInt64(addressBytes, 0);
        return true;
    }

    static string BluetoothAddressToMacString(ulong address)
    {
        var bytes = BitConverter.GetBytes(address).Take(6).Reverse().ToArray();
        return BitConverter.ToString(bytes).Replace("-", ":").ToLowerInvariant();
    }

    // ---- Args ----

    static CliArgs ParseArgs(string[] args)
    {
        var result = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            switch (a)
            {
                case "-?":
                case "/?":
                case "--help":
                case "-h":
                    result.Help = true;
                    break;
                case "--scan":
                    result.Scan = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int d) && d > 0)
                    {
                        result.ScanDurationMs = d * 1000;
                        i++;
                    }
                    break;
                case "--config":
                    if (i + 1 < args.Length) result.ConfigPath = args[++i];
                    break;
            }
        }
        return result;
    }

    static void PrintHelp()
    {
        Console.WriteLine(@"DynLockStandalone - 独立动态锁实现
用法:
  DynLockStandalone.exe                    按 dynlock.json 监控并自动锁屏
  DynLockStandalone.exe --scan [秒]        扫描附近 BLE 设备，默认 10 秒
  DynLockStandalone.exe --config <路径>    指定配置文件
  DynLockStandalone.exe --help             显示本帮助

首次使用步骤:
  1. 运行 --scan 找到你的手机/手环 MAC 地址
  2. 将 MAC 填入 dynlock.json 的 DeviceMac 字段
  3. 运行 DynLockStandalone.exe 开始监控
");
    }

    static void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Log("收到 Ctrl+C，正在停止...");
        Environment.Exit(0);
    }

    static void OnExit(object? sender, EventArgs e)
    {
        logWriter?.Flush();
        logWriter?.Dispose();
    }

    static string GetVersion() => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    class CliArgs
    {
        public bool Help { get; set; }
        public bool Scan { get; set; }
        public int ScanDurationMs { get; set; } = 10000;
        public string? ConfigPath { get; set; }
    }
}

public class DynLockConfig
{
    public string DeviceMac { get; set; } = "";
    public string? DeviceName { get; set; }
    public int LockThresholdDbm { get; set; } = -75;
    public int OutOfRangeTimeoutMs { get; set; } = 5000;
    public int LockDelayMs { get; set; } = 3000;
    public int ScanIntervalMs { get; set; } = 2000;
    public int RssiWindowSize { get; set; } = 5;
    public double RssiSmoothing { get; set; } = 0.7;
    public bool ActiveScan { get; set; } = true;
    public string? LogPath { get; set; }
}

[JsonSerializable(typeof(DynLockConfig))]
public partial class DynLockConfigContext : JsonSerializerContext { }
