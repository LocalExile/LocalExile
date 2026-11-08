# Start the servers for LAN or tunnel play. Run it from PowerShell, not by double-clicking:
#   cd "F:\local exile"
#   .\scripts\dev\lan.ps1 -Address <the address your players dial> [-PublicLoginPort <n>] [-PublicPort <n>]
#   .\scripts\dev\lan.ps1 -Stop       # stop this checkout's servers and clients
#   .\scripts\dev\lan.ps1 -NoClient   # servers only, no client on this PC
#   .\scripts\dev\lan.ps1 -DryRun     # print the command instead of running it
#
# -Address is what Auth and the area change tell a client to dial: this PC's LAN address for players on the same
# network, or the tunnel's node domain for players outside it. -PublicLoginPort and -PublicPort are only needed when the
# tunnel maps a public port that differs from the local one; leave them out for LAN play.
param(
    [string]$Address = '',
    [int]$PublicLoginPort = 0,
    [int]$PublicPort = 0,
    [switch]$NoClient,
    [switch]$Stop,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$live = Join-Path $repo 'scripts\dev\live.ps1'
if (-not (Test-Path $live)) { throw "Missing $live (this script belongs in the repository's scripts\dev)." }

# live.ps1 builds the servers with `dotnet`. A process started from Explorer or a scheduled task often has no SDK on its
# path, so `dotnet` resolves to the runtime-only copy in Program Files and the first build fails.
function Find-DotnetWithSdk {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($exe in $candidates) {
        if (-not (Test-Path $exe)) { continue }
        $sdks = & $exe --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and "$sdks".Trim()) { return (Split-Path $exe -Parent) }
    }
    return $null
}

$root = Find-DotnetWithSdk
if ($root) {
    $env:DOTNET_ROOT = $root
    $env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
    # The Gateway copies every module into %TEMP%\localexile-mod-copy before loading it. A folder left behind by a
    # process with a narrower token (a sandboxed shell, an elevated run) cannot be written into by a normal run, every
    # module fails to load, and the Gateway then refuses every instance connection ("the game module is not loaded").
    # Keeping the copies inside the repository avoids sharing that folder with another run.
    $tmp = Join-Path $repo 'data\tmp'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $env:TEMP = $tmp
    $env:TMP = $tmp
} else {
    Write-Warning 'No .NET with an SDK was found (the .NET 10 SDK is usually what is needed): the build step will fail.'
}

if ($Stop) {
    Write-Host 'Stopping this checkout''s servers and clients ...' -ForegroundColor Cyan
    & $live -Stop
    return
}

if (-not $Address) {
    Write-Host 'Give the address your players dial: -Address <address>' -ForegroundColor Yellow
    Write-Host '  LAN:    -Address <this PC''s LAN address>, e.g. -Address 192.168.1.20' -ForegroundColor DarkGray
    Write-Host '  tunnel: -Address <the tunnel node domain> -PublicLoginPort <n> -PublicPort <n>' -ForegroundColor DarkGray
    Write-Host 'See Agents\docs\dev\lan-play.md.' -ForegroundColor DarkGray
    exit 1
}

# live.ps1's parameters are passed by name: an array splat binds by position and would read '-NoExtract' as -Only's value.
$liveArgs = @{
    NoExtract = $true
    Lan = $true
    PublicAddress = $Address
}
if ($PublicLoginPort -gt 0) { $liveArgs.PublicLoginPort = $PublicLoginPort }
if ($PublicPort -gt 0) { $liveArgs.PublicPort = $PublicPort }
if ($NoClient) { $liveArgs.NoClient = $true }
$liveShown = @('live.ps1') + ($liveArgs.GetEnumerator() | ForEach-Object {
    if ($_.Value -is [bool]) { "-$($_.Key)" } else { "-$($_.Key) $($_.Value)" } })

Write-Host ''
Write-Host 'Players dial:' -ForegroundColor Cyan
Write-Host "  login    $Address : $(if ($PublicLoginPort -gt 0) { $PublicLoginPort } else { 'the local login port' })"
Write-Host "  instance $Address : $(if ($PublicPort -gt 0) { $PublicPort } else { 'the local instance port' })"
Write-Host '  patching is not tunnelled: the player tool answers the patch hello on their own PC'
Write-Host "  dotnet   $root"
Write-Host ''

if ($DryRun) {
    Write-Host "Would run: $($liveShown -join ' ')"
    return
}

$log = Join-Path $repo 'logs\lan-start.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
Write-Host "Logging to $log" -ForegroundColor DarkGray
# Tee keeps the output in the window and on disk for troubleshooting.
& $live @liveArgs 2>&1 | Tee-Object -FilePath $log -Append