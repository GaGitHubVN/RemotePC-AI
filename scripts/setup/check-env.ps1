<#
.SYNOPSIS  Reports the toolchain needed to build RemotePC AI and what is missing. Changes nothing.
#>
$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$jbr = 'C:\Program Files\Android\Android Studio\jbr'
$flutter = (Get-Command flutter -ErrorAction SilentlyContinue)?.Source ?? $(if (Test-Path 'D:\dev\flutter\bin\flutter.bat') { 'D:\dev\flutter\bin\flutter.bat' })
function Ver([scriptblock]$b) { try { (& $b 2>&1 | Select-Object -First 1).ToString().Trim() } catch { $null } }
$rows = @(
    @{ Tool = 'Windows'; Need = 'Windows 11 x64'; Have = [Environment]::OSVersion.VersionString },
    @{ Tool = '.NET SDK'; Need = '10.0.x'; Have = (& { try { dotnet --list-sdks 2>$null } catch { } } | Select-String '^10\.' | Select-Object -Last 1)?.Line },
    @{ Tool = 'Node.js'; Need = '>= 22.6'; Have = Ver { node --version } },
    @{ Tool = 'Git'; Need = 'any'; Have = Ver { git --version } },
    @{ Tool = 'GitHub CLI'; Need = 'for iOS builds'; Have = Ver { gh --version } },
    @{ Tool = 'Flutter'; Need = '3.50.x stable'; Have = $flutter ? (Ver { & $flutter --version } ) : $null },
    @{ Tool = 'JDK (Android Studio JBR)'; Need = '17+'; Have = (Test-Path "$jbr\bin\java.exe") ? (Ver { & "$jbr\bin\java.exe" -version }) : $null },
    @{ Tool = 'Android SDK build-tools'; Need = '36'; Have = (Get-ChildItem "$sdk\build-tools" -ErrorAction SilentlyContinue).Name -join ', ' },
    @{ Tool = 'Android cmdline-tools'; Need = 'latest'; Have = (Test-Path "$sdk\cmdline-tools\latest") ? 'installed' : $null },
    @{ Tool = 'Android NDK'; Need = '28.2.13676358'; Have = (Get-ChildItem "$sdk\ndk" -ErrorAction SilentlyContinue).Name -join ', ' },
    @{ Tool = 'Ollama (optional)'; Need = 'for Local AI'; Have = Ver { ollama --version } },
    @{ Tool = 'Docker (optional)'; Need = 'for signaling/TURN deploy'; Have = Ver { docker --version } }
)
$rows | ForEach-Object { [pscustomobject]@{ Tool = $_.Tool; Required = $_.Need; Found = $_.Have ?? 'MISSING' } } | Format-Table -AutoSize -Wrap
try { $p = Join-Path (Resolve-Path "$PSScriptRoot/../..") '.write-test'; Set-Content $p ''; Remove-Item $p; 'Project folder is writable.' } catch { 'Project folder is NOT writable.' }
