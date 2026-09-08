@echo off
title Fix Dynamic Lock - Clock and Bluetooth Reset
echo ================================================
echo   Fix Dynamic Lock
echo   Step 1: Enable auto time-sync (permanent)
echo   Step 2: Sync clock with NTP now
echo   Step 3: Restart Bluetooth stack
echo ================================================
echo.

echo [1/3] Enable Windows Time auto-start...
sc config w32time start= auto
echo.

echo [2/3] Start Windows Time and sync with NTP...
net start w32time
w32tm /config /syncfromflags:manual /manualpeerlist:"time.windows.com,0x1" /update
w32tm /resync /force
echo.

echo [3/3] Restart Bluetooth support service...
net stop bthserv
net start bthserv
echo.

echo ================================================
echo   Result:
echo ================================================
echo --- Time status ---
w32tm /query /status
echo.
echo --- Bluetooth service ---
sc query bthserv
echo.
echo Done. You may close this window.
pause
