# DynLockStandalone - 独立动态锁实现

与 Windows 原生「动态锁（Dynamic Lock）」对齐思路的独立程序版实现。通过监听指定手机/手环的 **BLE（低功耗蓝牙）广播 RSSI**，在信号持续变弱或设备消失后自动锁定 Windows 工作站。

> 参考开源项目：[bleunlock](https://github.com/departureszy/bleunlock)（BLE 距离感应自动锁屏/解锁）、[WAL](https://github.com/extdevil/WAL)（Windows Auto Lock）等实践经验。

---

## 目录

- [功能特性](#功能特性)
- [与 Windows 原生动态锁的差异](#与-windows-原生动态锁的差异)
- [系统要求与依赖](#系统要求与依赖)
- [编译步骤](#编译步骤)
- [运行步骤](#运行步骤)
- [配置说明](#配置说明)
- [部署方式](#部署方式)
- [注意事项与排障](#注意事项与排障)
- [项目结构](#项目结构)

---

## 功能特性

- **无需手机 App**：直接监听手机系统自带的 BLE 广播，手机端无需安装任何应用。
- **基于 RSSI 的距离判断**：支持中位数滤波 + 指数平滑，降低信号抖动造成的误触发。
- **自动锁屏**：当信号低于阈值或超过「丢失超时」后，经过可配置的「锁屏延迟」调用 `LockWorkStation()` 锁屏。
- **设备发现**：内置 `--scan` 模式，扫描附近 BLE 设备并显示 MAC、名称、平均 RSSI，方便填入配置文件。
- **纯控制台 + 文件日志**：适合手动运行、脚本调用或挂载到任务计划程序/服务中。
- **零第三方 NuGet 依赖**：仅使用 .NET 8 + Windows 自带的 WinRT 蓝牙 API。

---

## 与 Windows 原生动态锁的差异

| 维度 | Windows 原生 Dynamic Lock | DynLockStandalone（本实现） |
|---|---|---|
| **信号来源** | 经典蓝牙（Classic Bluetooth）连接 + 内部 RSSI | BLE（低功耗蓝牙）广播 RSSI |
| **是否需要配对** | 必须先在 Windows 设置中配对手机 | 不需要配对，只需知道设备 BLE MAC |
| **识别设备类型** | 仅识别 COD Major Device Class = 2 的手机 | 可监听任意 BLE 广播设备（手机、手环、耳机等） |
| **锁屏延迟** | 约 30–60 秒，不可调 | 完全可配置（默认丢失 5 秒 + 锁屏延迟 3 秒） |
| **自动解锁** | 不支持 | 不支持（自动解锁需要 Credential Provider / CDF，超出本程序范围） |
| **运行方式** | 系统集成在 `bthserv` 中 | 独立进程，可手动/计划任务/服务运行 |
| **安全性** | 依赖 Windows 登录会话 + 配对密钥 | 仅基于 BLE  proximity，MAC 可被伪造，属于「物理距离」级安全 |
| **兼容性** | 部分 Win11 24H2/25H2 用户反馈失效 | 只要 BLE 适配器正常、设备有广播即可工作 |

> **核心差异一句话总结**：原生动态锁用「经典蓝牙连接」判断手机是否在附近；本程序用「BLE 广播 RSSI」判断，响应更快、可调阈值，但监听的是 BLE 信号而非 Windows 内部使用的经典蓝牙信号。

---

## 系统要求与依赖

### 运行时依赖

- **操作系统**：Windows 10 版本 1809（Build 17763）或更高；Windows 11 推荐。
- **.NET 运行时**：.NET 8 运行时（框架依赖版）或无需运行时（自包含版）。
- **硬件**：支持 BLE 4.0+ 的蓝牙适配器（笔记本/台式机蓝牙模块）。
- **权限**：普通用户即可运行；锁屏调用 `LockWorkStation` 不需要管理员。

### 开发/编译依赖

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（已验证 8.0.424）
- Windows 10/11 64 位开发机
- 可选：Visual Studio 2022 / VS Code + C# Dev Kit

---

## 编译步骤

### 1. 进入项目目录

```powershell
cd <仓库目录>\src\DynLock
```

### 2. 还原并编译

```powershell
dotnet restore
dotnet build -c Release
```

### 3. 发布为单文件（推荐）

**框架依赖版**（体积小，需目标机安装 .NET 8 运行时）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

产物位于：

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DynLockStandalone.exe
```

**自包含版**（体积大，无需目标机装运行时）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false
```

> 注意：开启 `PublishTrimmed=true` 可能导致 WinRT 投影被裁剪，建议先关闭.Trimmed。

---

## 运行步骤

### 首次使用：找到你的设备 MAC

```powershell
.\DynLockStandalone.exe --scan 10
```

输出示例：

```text
开始扫描 BLE 设备，持续 10 秒...
扫描结束。发现的设备：

MAC                名称                         平均 RSSI      样本数
----------------------------------------------------------------------
AA:BB:CC:11:22:33  Android Phone                -45.2 dBm        12
AA:BB:CC:11:22:44  示例蓝牙耳机         -58.3 dBm         3

提示: 将目标设备的 MAC 填入 dynlock.json 的 DeviceMac 字段即可使用。
```

> 安卓手机通常同时有「经典蓝牙 MAC」和「BLE 随机/公开 MAC」，二者可能不同。这里要填 **扫描结果里出现、且信号稳定的那个 MAC**。

### 编辑配置文件

第一次运行任何命令后，程序会在 exe 同目录生成 `dynlock.json`：

```json
{
  "DeviceMac": "AA:BB:CC:11:22:33",
  "DeviceName": "Android Phone",
  "LockThresholdDbm": -75,
  "OutOfRangeTimeoutMs": 5000,
  "LockDelayMs": 3000,
  "ScanIntervalMs": 2000,
  "RssiWindowSize": 5,
  "RssiSmoothing": 0.7,
  "ActiveScan": true,
  "LogPath": null
}
```

将 `DeviceMac` 替换为你扫描到的 MAC，保存。

### 启动监控

```powershell
.\DynLockStandalone.exe
```

程序会：
1. 启动 BLE 扫描；
2. 过滤目标 MAC 的广播；
3. 实时计算平滑 RSSI；
4. 当 RSSI 低于阈值或超时未收到广播时，先等待 `LockDelayMs`，然后调用 `LockWorkStation()`。

按 `Ctrl+C` 停止。

### 指定配置文件路径

```powershell
.\DynLockStandalone.exe --config "D:\Config\dynlock.json"
```

---

## 配置说明

| 字段 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `DeviceMac` | string | `""` | 目标设备 BLE MAC，格式 `AA:BB:CC:DD:EE:FF` 或 `AABBCCDDEEFF` |
| `DeviceName` | string? | `null` | 仅用于日志展示 |
| `LockThresholdDbm` | int | `-75` | RSSI 低于此值视为「离开范围」。建议 -70 ~ -85 |
| `OutOfRangeTimeoutMs` | int | `5000` | 连续多久没收到广播即视为离开（毫秒） |
| `LockDelayMs` | int | `3000` | 确认离开后多久执行锁屏（防抖，毫秒） |
| `ScanIntervalMs` | int | `2000` | 状态评估周期（毫秒） |
| `RssiWindowSize` | int | `5` | 中位数滤波窗口大小 |
| `RssiSmoothing` | double | `0.7` | 指数平滑系数（越大历史权重越高，范围 0–0.99） |
| `ActiveScan` | bool | `true` | 是否主动请求 Scan Response（耗电略高，但能拿到 LocalName） |
| `LogPath` | string? | `null` | 日志文件路径；`null` 表示 `dynlock.log`（exe 同目录） |

### 阈值调参建议

- 手机放在桌面上：RSSI 通常在 -40 ~ -60 dBm
- 起身走到办公室门口：RSSI 可能降到 -70 ~ -85 dBm
- 离开房间/关闭蓝牙：无广播或 RSSI < -90 dBm

建议先运行 `--scan` 观察目标设备在不同位置的 RSSI，再设置 `LockThresholdDbm`。

---

## 部署方式

### 方式一：手动运行（测试/个人使用）

直接双击 `DynLockStandalone.exe`，或从 PowerShell/终端启动。按 `Ctrl+C` 停止。

### 方式二：开机自启（任务计划程序）

不需要管理员，按当前用户登录后自动运行：

```powershell
$action = New-ScheduledTaskAction -Execute "C:\Tools\DynLockStandalone\DynLockStandalone.exe" -WorkingDirectory "C:\Tools\DynLockStandalone"
$trigger = New-ScheduledTaskTrigger -AtLogon
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName "DynLockStandalone" -Action $action -Trigger $trigger -Settings $settings -Description "BLE 动态锁"
```

### 方式三：作为 Windows 服务运行（需额外封装）

纯控制台程序无法直接响应服务控制管理器（SCM）。如需作为服务，推荐两种方案：

1. **使用 nssm（推荐）**：
   ```powershell
   nssm install DynLockStandalone "C:\Tools\DynLockStandalone\DynLockStandalone.exe"
   nssm set DynLockStandalone AppDirectory "C:\Tools\DynLockStandalone"
   nssm start DynLockStandalone
   ```

2. **改写为 Worker Service**：将项目 SDK 改为 `Microsoft.NET.Sdk.Worker`，安装 `Microsoft.Extensions.Hosting.WindowsServices`，实现 `BackgroundService`。

> 服务运行在无桌面会话中，`LockWorkStation` 在远程桌面/服务会话中行为可能不同，建议优先用「方式二」任务计划程序。

---

## 注意事项与排障

### 1. 扫描不到目标设备

- 确认手机蓝牙已开启。
- 部分安卓手机在屏幕关闭后会停止 BLE 广播或随机化 MAC，可在「开发者选项」中关闭 BLE 隐私/地址随机化。
- 某些耳机/手环在配对连接后可能停止公开广播；手机通常始终会广播。
- 尝试切换 `ActiveScan` 为 `false`。

### 2. 误锁屏 / 不锁屏

- 调整 `LockThresholdDbm` 和 `OutOfRangeTimeoutMs`。
- 观察 `dynlock.log` 中的 RSSI 曲线，找到适合你环境的阈值。
- 增大 `RssiWindowSize` 或 `RssiSmoothing` 可减少抖动。

### 3. 运行时报错「无法加载 DLL / WinRT」

- 确认目标机已安装 .NET 8 运行时（框架依赖版）。
- 确认 Windows 版本 ≥ 1809。
- 自包含版部署时建议关闭 `PublishTrimmed`。

### 4. 安全性提示

- BLE MAC 可以被伪造；本程序提供的是「物理 proximity」级别的保护，不适合高安全场景。
- 锁屏后会进入 Windows 登录界面，不会自动解锁。
- 建议配合 Windows Hello / PIN 使用。

### 5. 与现有「锁屏延迟设置便携版」的关系

- 本项目是**独立的动态锁实现**，不修改 Windows 原生的 `EnableGoodbye` / `DevicePairing` 注册表项。
- 可同时运行，互不干扰；但两者都调用 `LockWorkStation()`，不会产生冲突。

---

## 项目结构

```
DynLockStandalone/
├── DynLockStandalone.csproj   # 项目文件
├── Program.cs                 # 核心代码（配置、扫描、监控、锁屏）
├── README.md                  # 本说明
└── dynlock.json               # 运行后生成的配置文件（可编辑）
```

---

## 许可证

本项目代码可自由使用、修改。使用本程序造成的任何数据丢失或安全问题由使用者自行承担。
