<#
.SYNOPSIS  Runs the mobile end-to-end test on a local Android emulator against tools/RemotePC.TestHost.
.DESCRIPTION
  Requires an AVD (default "remotepc_test", Android 14 x86_64) and the Android SDK in %LOCALAPPDATA%\Android\Sdk.
  The test host is CI/test-only: it auto-approves pairing and replays a synthetic H.264 clip. Screenshots go to
  .e2e-android\ (git-ignored).
.EXAMPLE   pwsh scripts/test/mobile-e2e-android.ps1
#>
param([string]$Avd = 'remotepc_test')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$env:PATH = "$sdk\platform-tools;$env:PATH"
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'
$flutter = (Get-Command flutter -ErrorAction SilentlyContinue)?.Source ?? 'D:\dev\flutter\bin\flutter.bat'

if (-not ((adb devices) -match 'emulator-\d+\s+device')) {
    Start-Process "$sdk\emulator\emulator.exe" -ArgumentList "-avd $Avd -no-snapshot -no-boot-anim -gpu swiftshader_indirect -no-audio" -WorkingDirectory "$sdk\emulator"
    adb wait-for-device
    for ($i = 0; $i -lt 120 -and -not ((adb shell getprop sys.boot_completed 2>$null) -match '1'); $i++) { Start-Sleep 2 }
}
$shots = Join-Path $root '.e2e-android'
New-Item -ItemType Directory -Force $shots | Out-Null
dotnet build (Join-Path $root 'apps/windows-host/tools/RemotePC.TestHost') -c Release | Out-Null
$hostLog = Join-Path $shots 'testhost.log'
$th = Start-Process dotnet -ArgumentList "`"$root/apps/windows-host/tools/RemotePC.TestHost/bin/Release/net10.0/RemotePC.TestHost.dll`" 45900 `"$env:TEMP\pair.txt`" `"$shots`"" `
    -RedirectStandardOutput $hostLog -PassThru -WindowStyle Hidden
try {
    Start-Sleep 4
    adb reverse tcp:45900 tcp:45900 | Out-Null
    adb reverse tcp:45901 tcp:45901 | Out-Null
    $defines = Join-Path $env:TEMP 'e2e-defines.json'
    '{"PAIR_HTTP":"http://127.0.0.1:45901/","EXPECT_STYLE":"material"}' | Set-Content $defines
    Push-Location (Join-Path $root 'apps/mobile')
    & $flutter test integration_test/app_test.dart -d (((adb devices) -match 'emulator-\d+' | Select-Object -First 1) -split '\s')[0] "--dart-define-from-file=$defines"
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw "integration test failed ($code)" }
    foreach ($p in 'pairing result ok=True', 'INPUT button left', 'upload completed upload-test.txt') {
        if (-not (Select-String -Path $hostLog -SimpleMatch $p -Quiet)) { throw "host did not log: $p" }
    }
    "E2E PASSED — screenshots in $shots"
} finally {
    Stop-Process -Id $th.Id -Force -ErrorAction SilentlyContinue
}
