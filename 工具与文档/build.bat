@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0.."

rem ============ 重新打包 锁屏设置.exe（C# 单文件版 v3.0）============
rem 需要：.NET 8 SDK（含 Windows Desktop 运行时）
rem 本脚本位于「工具与文档」子目录，所有路径通过 %~dp0.. 回退到项目根目录。

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 找不到 dotnet，请安装 .NET 8 SDK。
    exit /b 1
)

echo.
echo [1/2] 编译并发布（单文件，框架依赖）...
cd /d "%~dp0..\src\gui"
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
if errorlevel 1 ( echo [错误] 编译失败 & exit /b 1 )

echo [2/2] 拷贝到根目录 ...
copy /y "bin\Release\net8.0-windows\win-x64\publish\LockscreenGui.exe" "..\..\锁屏设置.exe" >nul
if errorlevel 1 ( echo [错误] 拷贝失败 & exit /b 1 )

cd /d "%~dp0.."
echo.
echo 打包完成:
echo   锁屏设置.exe（单文件 C#，约 0.2MB，需目标机安装 .NET 8 桌面运行时）
echo.
endlocal
