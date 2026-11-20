<#
.SYNOPSIS  Builds, tests and packages the Windows Host portable release.
.EXAMPLE   pwsh scripts/release/package-windows.ps1 [-SkipTests]
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$sln = Join-Path $root 'apps/windows-host'
if (-not $SkipTests) {
    dotnet test (Join-Path $sln 'tests/RemotePC.Core.Tests') --filter "Category!=LocalAI" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
}
$pub = Join-Path $root 'artifacts/publish'
Remove-Item -Recurse -Force $pub -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $sln 'src/RemotePC.Host/obj') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $sln 'src/RemotePC.Host') -c Release -o $pub
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# Smoke test from an isolated folder (proves no dependency on the build tree).
$smoke = Join-Path ([IO.Path]::GetTempPath()) "rpc-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $smoke | Out-Null
Copy-Item (Join-Path $pub 'RemotePC-AI.exe') $smoke
$p = Start-Process (Join-Path $smoke 'RemotePC-AI.exe') -ArgumentList '--smoke-test' -PassThru
if (-not $p.WaitForExit(90000)) { $p.Kill(); throw 'smoke test timed out' }
if ($p.ExitCode -ne 0) { throw "smoke test exit code $($p.ExitCode)" }
if (-not (Select-String -Path (Join-Path $smoke 'data/logs/*.jsonl') -Pattern 'smoke test OK' -Quiet)) { throw 'smoke log missing' }
Remove-Item -Recurse -Force $smoke

$out = Join-Path $root 'releases/windows'
$stage = Join-Path $out 'RemotePC-AI-Windows-x64-Portable'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $pub 'RemotePC-AI.exe') $stage
Copy-Item (Join-Path $root 'docs/user-guide/WINDOWS_QUICKSTART.md') (Join-Path $stage 'README.md') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'LICENSE') $stage -ErrorAction SilentlyContinue
$zip = Join-Path $out 'RemotePC-AI-Windows-x64-Portable.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage/*" -DestinationPath $zip
$lines = foreach ($f in @($zip, (Join-Path $stage 'RemotePC-AI.exe'))) {
    "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf)
}
Set-Content (Join-Path $out 'SHA256SUMS.txt') $lines
Remove-Item -Recurse -Force $stage
Get-Content (Join-Path $out 'SHA256SUMS.txt')
