<#
.SYNOPSIS  Runs every automated test suite and prints a summary table.
.PARAMETER IncludeHardware  Windows capture/encode/UIA tests (needs an interactive desktop).
.PARAMETER IncludeLocalAI   Tests against a real Ollama with a tool-capable model.
#>
param([switch]$IncludeHardware, [switch]$IncludeLocalAI)
$ErrorActionPreference = 'Continue'
$root = Resolve-Path "$PSScriptRoot/../.."
$flutter = (Get-Command flutter -ErrorAction SilentlyContinue)?.Source ?? 'D:\dev\flutter\bin\flutter.bat'
$results = [System.Collections.Generic.List[object]]::new()

function Run($name, $dir, [scriptblock]$cmd, [string]$pattern) {
    Push-Location $dir
    $out = & $cmd 2>&1 | Out-String
    $code = $LASTEXITCODE
    Pop-Location
    $summary = ([regex]::Matches($out, $pattern) | Select-Object -Last 1).Value
    $results.Add([pscustomobject]@{ Suite = $name; Result = $(if ($code -eq 0) { 'PASSED' } else { 'FAILED' }); Summary = $summary.Trim() })
    if ($code -ne 0) { Write-Host $out -ForegroundColor Red }
}

Run 'Signaling (TypeScript)' "$root/backend/signaling" { npm test --silent } 'Tests\s+\d+ passed[^\n]*'
Run 'Windows Core (.NET)' "$root/apps/windows-host" { dotnet test tests/RemotePC.Core.Tests --filter "Category!=LocalAI" } '(Passed|Failed)!.*'
if ($IncludeHardware) { Run 'Windows Platform (hardware)' "$root/apps/windows-host" { dotnet test tests/RemotePC.Platform.Windows.Tests --filter "Category!=LocalAI" } '(Passed|Failed)!.*' }
if ($IncludeLocalAI) {
    Run 'Local AI (Core)' "$root/apps/windows-host" { dotnet test tests/RemotePC.Core.Tests --filter "Category=LocalAI" } '(Passed|Failed)!.*'
    Run 'Local AI agent (Platform)' "$root/apps/windows-host" { dotnet test tests/RemotePC.Platform.Windows.Tests --filter "Category=LocalAI" } '(Passed|Failed)!.*'
}
Run 'Mobile (Dart)' "$root/apps/mobile" { & $flutter test } '\+\d+[^\n]*(All tests passed|Some tests failed)[^\n]*'
$results | Format-Table -AutoSize
if ($results.Result -contains 'FAILED') { exit 1 }
