#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$AndroidHome  = "C:\android"
$CmdlineToolsUrl = "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip"
$CmdlineToolsZip = "$env:TEMP\android-cmdline-tools.zip"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  CryptoDecision Mobile - Android Build Setup  " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

# ── Step 1: Install JDK 17 ────────────────────────────────────────────────────
Write-Host ""
Write-Host "[1/7] Checking Java JDK..." -ForegroundColor Yellow
$javaOk = $false
try {
    $javaVer = (& java -version 2>&1) | Out-String
    $pattern = '"(17|18|19|20|21)\.'
    if ($javaVer -match $pattern) {
        Write-Host "  OK  Java already installed (v$($Matches[1]))" -ForegroundColor Green
        $javaOk = $true
    }
} catch {}

if (-not $javaOk) {
    Write-Host "  Installing Microsoft OpenJDK 17 via winget..." -ForegroundColor Gray
    winget install --id Microsoft.OpenJDK.17 --accept-source-agreements --accept-package-agreements --silent
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH","User")
    Write-Host "  OK  JDK 17 installed" -ForegroundColor Green
}

# Find JAVA_HOME
$javaExe = (Get-Command java -ErrorAction SilentlyContinue)
if ($javaExe) {
    $javaHome = Split-Path (Split-Path $javaExe.Source)
} else {
    $javaHome = ""
}
if ($javaHome -and (Test-Path $javaHome)) {
    [Environment]::SetEnvironmentVariable("JAVA_HOME", $javaHome, "User")
    $env:JAVA_HOME = $javaHome
    Write-Host "  JAVA_HOME = $javaHome" -ForegroundColor Gray
}

# ── Step 2: Android cmdline-tools ─────────────────────────────────────────────
Write-Host ""
Write-Host "[2/7] Setting up Android SDK..." -ForegroundColor Yellow
$sdkManager = "$AndroidHome\cmdline-tools\latest\bin\sdkmanager.bat"

if (Test-Path $sdkManager) {
    Write-Host "  OK  Android cmdline-tools already present" -ForegroundColor Green
} else {
    Write-Host "  Downloading Android cmdline-tools (~130 MB)..." -ForegroundColor Gray
    Invoke-WebRequest -Uri $CmdlineToolsUrl -OutFile $CmdlineToolsZip -UseBasicParsing

    Write-Host "  Extracting..." -ForegroundColor Gray
    New-Item -ItemType Directory -Force -Path "$AndroidHome\cmdline-tools" | Out-Null
    Expand-Archive -Path $CmdlineToolsZip -DestinationPath "$AndroidHome\cmdline-tools\_extract" -Force

    $extracted = Get-ChildItem "$AndroidHome\cmdline-tools\_extract" -Directory | Select-Object -First 1
    if (Test-Path "$AndroidHome\cmdline-tools\latest") {
        Remove-Item "$AndroidHome\cmdline-tools\latest" -Recurse -Force
    }
    Move-Item $extracted.FullName "$AndroidHome\cmdline-tools\latest"
    Remove-Item "$AndroidHome\cmdline-tools\_extract" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $CmdlineToolsZip -Force -ErrorAction SilentlyContinue

    Write-Host "  OK  cmdline-tools extracted" -ForegroundColor Green
}

[Environment]::SetEnvironmentVariable("ANDROID_HOME", $AndroidHome, "User")
$env:ANDROID_HOME = $AndroidHome

$pathsToAdd = @("$AndroidHome\cmdline-tools\latest\bin", "$AndroidHome\platform-tools")
$userPath = [Environment]::GetEnvironmentVariable("PATH","User")
if (-not $userPath) { $userPath = "" }
foreach ($p in $pathsToAdd) {
    if ($userPath -notlike "*$p*") { $userPath = "$p;$userPath" }
}
[Environment]::SetEnvironmentVariable("PATH", $userPath, "User")
$env:PATH = ($pathsToAdd -join ";") + ";$env:PATH"

Write-Host "  ANDROID_HOME = $AndroidHome" -ForegroundColor Gray

# ── Step 3: Accept licenses ───────────────────────────────────────────────────
Write-Host ""
Write-Host "[3/7] Accepting Android SDK licenses..." -ForegroundColor Yellow
"y`ny`ny`ny`ny`ny`ny`ny" | & $sdkManager --licenses --sdk_root=$AndroidHome 2>&1 | Out-Null
Write-Host "  OK  Licenses accepted" -ForegroundColor Green

# ── Step 4: Install SDK packages ──────────────────────────────────────────────
Write-Host ""
Write-Host "[4/7] Installing Android SDK packages (~1 GB, may take 10+ min)..." -ForegroundColor Yellow
$packages = @("platform-tools", "platforms;android-34", "build-tools;34.0.0")
foreach ($pkg in $packages) {
    Write-Host "  Installing: $pkg" -ForegroundColor Gray
    & $sdkManager $pkg --sdk_root=$AndroidHome 2>&1 | ForEach-Object {
        if ($_ -match "\[=+>" -or $_ -match "100%" -or $_ -match "done") { Write-Host "    $_" }
    }
}
Write-Host "  OK  SDK packages installed" -ForegroundColor Green

# ── Step 5: npm install ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "[5/7] Installing Capacitor (npm install)..." -ForegroundColor Yellow
Set-Location $ScriptDir
npm install --silent
Write-Host "  OK  npm packages installed" -ForegroundColor Green

# ── Step 6: Capacitor add android + sync ──────────────────────────────────────
Write-Host ""
Write-Host "[6/7] Initializing Capacitor Android project..." -ForegroundColor Yellow

if (-not (Test-Path "$ScriptDir\android")) {
    npx cap add android 2>&1 | Write-Host
    Write-Host "  OK  Android platform added" -ForegroundColor Green
} else {
    Write-Host "  OK  Android platform already exists" -ForegroundColor Green
}

# Patch AndroidManifest.xml for cleartext HTTP + network security config
$manifestPath = "$ScriptDir\android\app\src\main\AndroidManifest.xml"
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw
    if ($manifest -notmatch "networkSecurityConfig") {
        $manifest = $manifest -replace '(<application)', '<application android:networkSecurityConfig="@xml/network_security_config"'
        Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
    }
    if ($manifest -notmatch "usesCleartextTraffic") {
        $manifest = $manifest -replace '(<application)', '$1 android:usesCleartextTraffic="true"'
        Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8
    }
}

# Write network_security_config.xml
$netSecDir = "$ScriptDir\android\app\src\main\res\xml"
New-Item -ItemType Directory -Force -Path $netSecDir | Out-Null
@"
<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
    <base-config cleartextTrafficPermitted="true">
        <trust-anchors>
            <certificates src="system" />
        </trust-anchors>
    </base-config>
</network-security-config>
"@ | Set-Content "$netSecDir\network_security_config.xml" -Encoding UTF8

npx cap sync android 2>&1 | Where-Object { $_ -match "(Sync|Copy|update)" } | Write-Host
Write-Host "  OK  Capacitor sync done" -ForegroundColor Green

# ── Step 7: Build APK ─────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[7/7] Building debug APK..." -ForegroundColor Yellow
Set-Location "$ScriptDir\android"

$env:ANDROID_HOME = $AndroidHome
if ($javaHome) { $env:JAVA_HOME = $javaHome }

$gradleOut = & .\gradlew.bat assembleDebug --no-daemon 2>&1
$apkSrc    = "app\build\outputs\apk\debug\app-debug.apk"

if (Test-Path $apkSrc) {
    $dest = "$ScriptDir\..\CryptoDecision-debug.apk"
    Copy-Item $apkSrc $dest -Force
    $sizeMB = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    Write-Host ""
    Write-Host "================================================" -ForegroundColor Green
    Write-Host "  APK BUILT SUCCESSFULLY!" -ForegroundColor Green
    Write-Host "  File: CryptoDecision-debug.apk ($sizeMB MB)" -ForegroundColor Green
    Write-Host "  Path: $(Resolve-Path $dest)" -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Install on Android phone:" -ForegroundColor Cyan
    Write-Host "  1. Copy CryptoDecision-debug.apk to your phone"
    Write-Host "  2. Settings > Security > Install unknown apps -> Enable for Files app"
    Write-Host "  3. Open the APK file -> Install"
    Write-Host "  4. Server: http://123.20.59.4:8080 (already set in config.js)"
} else {
    Write-Host "  FAILED - APK not found at $apkSrc" -ForegroundColor Red
    Write-Host "  Last 20 lines of gradle output:" -ForegroundColor Red
    $gradleOut | Select-Object -Last 20 | ForEach-Object { Write-Host "    $_" }
    exit 1
}

Set-Location $ScriptDir
