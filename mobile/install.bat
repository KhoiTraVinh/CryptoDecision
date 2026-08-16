@echo off
echo ========================================
echo  CryptoDecision - ADB Installer
echo ========================================
echo.

set ADB=C:\Android\platform-tools\adb.exe
set APK=D:\dotnet-distributed-systems\CryptoDecision\mobile\CryptoDecision-latest.apk
set PKG=com.cryptodecision.mobile

echo [1] Checking connected device...
"%ADB%" devices
echo.

echo [2] Uninstalling old version (if any)...
"%ADB%" uninstall %PKG%
echo.

echo [3] Installing new APK...
"%ADB%" install "%APK%"

if %ERRORLEVEL% == 0 (
    echo.
    echo [OK] Installed! Launching app...
    "%ADB%" shell monkey -p %PKG% 1
) else (
    echo.
    echo [FAIL] Check USB Debugging is ON, then run again.
)
pause
