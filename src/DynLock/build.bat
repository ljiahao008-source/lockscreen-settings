@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [DynLockStandalone] 正在发布单文件版本（框架依赖）...
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true

if %errorlevel% neq 0 (
    echo [DynLockStandalone] 发布失败。
    pause
    exit /b %errorlevel%
)

echo [DynLockStandalone] 发布完成。
echo 产物路径：
echo   %~dp0bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DynLockStandalone.exe
pause
