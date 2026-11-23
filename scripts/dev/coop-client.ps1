# Co-op client for connecting to a friend's LocalExile server over Tailscale.
#
# The client only ever dials 127.0.0.1: the login address is written into the GGPK by live.ps1 and the patch server
# is passed on the command line. A `netsh interface portproxy` rule on this machine forwards 127.0.0.1:20493 (login)
# and 127.0.0.1:6112 (gateway) to the host's Tailscale address, so the client logs in to the friend's server while
# this checkout runs only its Patch server (12995/8080), which serves the same 3.29.3c files.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev\coop-client.ps1 [-HostIp <tailscale-ip>]
# Stays alive: the servers and the client are children of this process.

param([string]$HostIp = "100.100.218.89")

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Split-Path -Parent $repo
# The tools live beside the checkout; derive them so no non-ASCII path is written into this file.
$env:PATH = (Join-Path $root 'node\node-v24.4.1-win-x64') + ';' + (Join-Path $root 'dotnet-sdk') + ';' +
    (Join-Path $root 'python') + ';' + $env:PATH
$live = Join-Path $repo 'scripts\dev\live.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Say($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

Say "dotnet: $((Get-Command dotnet -ErrorAction SilentlyContinue).Source)"

# 1. The proxy must be in place: without it the client talks to nothing.
foreach ($port in 20493, 6112)
{
    $up = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
    Say "bridge 127.0.0.1:${port} -> ${HostIp}:${port} : $(if ($up) { 'up' } else { 'DOWN' })"
    if (-not $up)
    {
        Say 'run this once in an admin PowerShell, then retry:'
        Say "  netsh interface portproxy add v4tov4 listenaddress=127.0.0.1 listenport=$port connectaddress=$HostIp connectport=$port"
        exit 1
    }
}

# 2. The local Patch server (the client is launched with --patching-server 127.0.0.1:12995).
#    live.ps1 -Only Patch starts it and returns; the server is its own process. Its output goes to a log so a failed
#    start can be read back here.
Say 'starting the local Patch server ...'
$patchLog = Join-Path $repo 'logs\coop-patch.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $patchLog) | Out-Null
$null = Start-Process -FilePath $ps -PassThru -WindowStyle Hidden `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $live, '-Only', 'Patch', '-NoClient', '-NoExtract', '-NoHotReload') `
    -RedirectStandardOutput $patchLog -RedirectStandardError (Join-Path $repo 'logs\coop-patch.err')
$deadline = (Get-Date).AddMinutes(5)
$up = $false
while ((Get-Date) -lt $deadline)
{
    if (@(Get-NetTCPConnection -LocalPort 12995 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) { $up = $true; break }
    if (@(Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) { $up = $true; break }
    Start-Sleep -Seconds 3
}
if (-not $up)
{
    Say 'the Patch server did not come up; its output:'
    Get-Content $patchLog -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { Say "  $_" }
    Get-Content (Join-Path $repo 'logs\coop-patch.err') -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { Say "  $_" }
    exit 1
}
Say 'patch server is listening (12995/8080)'

# 3. The client: live.ps1 -ClientOnly writes 127.0.0.1:20493 into the GGPK (the bridge sends it to the host) and
#    starts the client with the local patch server.
Say "opening the client; it logs in to $HostIp through the bridge ..."
$client = Start-Process -FilePath $ps -PassThru -WindowStyle Hidden `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $live, '-ClientOnly')
$null = $client.WaitForExit(240000)
Say "client launcher exit=$($client.ExitCode)"

# 4. Stay alive so the servers and the client are not killed with this process.
Say 'running; close the client window when done.'
while ($true) { Start-Sleep -Seconds 30 }
