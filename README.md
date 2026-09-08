# 锁屏设置（便携版 v3.1）

单文件 C# WinForms 工具：集中管理 Windows 锁屏相关的电源设置、屏幕保护与动态锁。GUI 与 CLI 同一 exe，直连 powercfg 与注册表，无后台进程、无端口，关窗即退。

## 下载

直接下载根目录的 [`锁屏设置.exe`](./锁屏设置.exe)（约 270KB，需目标机安装 .NET 8 桌面运行时）。

## 功能

- **电源设置**：锁屏后黑屏 / 关闭显示器 / 睡眠 / 休眠（AC/DC 分独立设置，预设 + 自定义 + 同步）
- **屏幕保护**：开关 / 等待时间 / 预设，注册表 + SystemParametersInfo 即时生效
- **动态锁**：开关 / 信任设备 / 蓝牙密钥健康度检查；自动补选信任设备；兼容 Win11 25H2「离开时锁定」独立开关（关闭动态锁时一并关闭）

## CLI 用法

```
锁屏设置.exe display=10min      直接设置（设完即退）
锁屏设置.exe --status           查看当前所有设置
锁屏设置.exe saver=5min         屏幕保护
锁屏设置.exe dynlock=on|off     动态锁
锁屏设置.exe --export=文件.json  导出配置（换机迁移）
锁屏设置.exe --import=文件.json  导入配置
```

支持 `ac:` / `dc:` 前缀只改接通电源 / 使用电池；时间写法 `never / 30s / 10min / 2h / 600`（纯数字=秒）。

## 构建与打包

```
工具与文档\build.bat
```

需要 .NET 8 SDK。图标由 `src/IconMaker` 程序化绘制（改图标 → 运行 IconMaker 重新生成 assets\app.ico）。

## 目录结构

```
锁屏设置.exe          主程序（GUI + CLI）
src/gui/              主程序源码（MainForm / Ui 视觉层 / Power / Saver / Dynlock / Config / Cli）
src/DynLock/          动态锁独立辅助工具
src/IconMaker/        图标生成器（System.Drawing 绘制 → ICO）
src/IcoMaker/         PNG → ICO 打包器
src/ShortcutMaker/    快捷方式重建工具
工具与文档/            build.bat、使用说明
legacy/               v2.1 Node.js 历史版本（仅存档）
data/                 示例配置、诊断文档、界面截图
backup/               v3.0 源码与图标备份
```

详细说明见 [工具与文档/使用说明.txt](./工具与文档/使用说明.txt)。
