# One run against the official client (see Agents/GettingStarted.md):
#   checks the one-time setup, stops anything on the dev ports and the patched client, builds Auth, Patch and Gateway,
#   starts all three (logs in logs/live-*.log) and launches Client/Game.patched.exe through the local patching server.
#   -NoClient   keep a running client (it drops to the login screen; log in again)
#   -NoBuild    skip the builds
#   -NoExtract  skip scripts/dev/extract-data.ps1, which otherwise first extracts any missing or outdated game data into
#               data/ (a quick check when nothing is missing)
#   -Walkmaps   also run scripts/walkmaps/walkmaps.py (needs Python + `pip install frida`): captures the walk map of every
#               area the client generates into data/walkability/
#   -Only Gateway[,Auth,Patch]   rebuild and restart only these servers; the others and the client keep running (implies
#               -NoClient). Restarting the Gateway drops the client to the login screen, but Auth keeps the login session.
param([switch]$NoClient, [switch]$NoBuild, [switch]$NoExtract, [switch]$Walkmaps, [switch]$ClientOnly, [ValidateSet('Auth', 'Patch', 'Gateway')][string[]]$Only = @())

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force $out | Out-Null

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }
function Select-LocalGateway {
    # The client can use a cached gateway list even when the patch welcome needs no download.
    & dotnet (Join-Path $repo 'src\GameServer.Patch\bin\Debug\net10.0\GameServer.Patch.dll') gateway (Join-Path $repo 'Client\Content.ggpk') --login '127.0.0.1:20493' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not select local login server; close all clients and retry' }
}
Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Test-Path (Join-Path $repo 'data\login-keys.json')) 'data\login-keys.json missing: run GameServer.Auth keygen'
Need (Test-Path (Join-Path $repo 'Client\Game.exe')) 'Client\Game.exe missing: copy the client and its PathOfExile.exe'
Need (Test-Path (Join-Path $repo 'Client\Game.patched.exe')) 'Client\Game.patched.exe missing: run GameServer.Auth patch'
Need (Test-Path (Join-Path $repo 'Client\Content.ggpk')) 'Client\Content.ggpk missing: this needs the standalone client layout'

# The control bridge can open a closed client without restarting the servers or touching an existing game.
if ($ClientOnly) {
    if ($NoClient -or $Only -or $Walkmaps) { throw '-ClientOnly cannot be combined with -NoClient, -Only or -Walkmaps' }
    $existing = @(Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue)
    if ($existing.Count) {
        Need ($existing.Count -eq 1 -and $existing[0].Path -eq (Join-Path $repo 'Client\Game.patched.exe')) 'another patched client is running'
        Write-Output "client already running: $($existing[0].Id)"
        return
    }
    foreach ($port in @(12995, 20493, 6112)) {
        Need (@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) "local servers must be running before -ClientOnly (missing port $port)"
    }
    Select-LocalGateway
    $p = Start-Process (Join-Path $repo 'Client\Game.patched.exe') -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory (Join-Path $repo 'Client') -PassThru
    Write-Output "client pid $($p.Id)"
    return
}

$python = $null
if ($Walkmaps) {
    # A virtual environment in the repository (.venv) wins over the python on PATH.
    $venv = Join-Path $repo '.venv\Scripts\python.exe'
    $python = if (Test-Path $venv) { $venv } else { (Get-Command python -ErrorAction SilentlyContinue).Source }
    if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
    Need $python 'Python not found (needed for -Walkmaps)'
    & $python -c 'import frida' 2>$null
    Need ($LASTEXITCODE -eq 0) 'the frida Python package is missing: python -m pip install frida'
}

# Game data from the GGPK (data/tables, object-types.json, ...): before the servers start, since they read it once.
if (-not $NoExtract) { & (Join-Path $PSScriptRoot 'extract-data.ps1') }

# Without this the Gateway stops after creating the player and the client never enters the area.
if (-not $env:Game_GATEWAY_ENTER) { $env:Game_GATEWAY_ENTER = '1' }

$servers = if ($Only) { $Only } else { @('Auth', 'Patch', 'Gateway') }
if ($Only) { $NoClient = $true }
$ports = @{ Auth = @(20493); Patch = @(12995, 8080); Gateway = @(6112) }

if (-not $NoClient) { Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue | Stop-Process -Force }
if (-not $Only) {
    Get-CimInstance Win32_Process -Filter "Name like 'python%'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*walkmaps.py*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
# Stop only our own servers (dotnet) on the dev ports; anything else there is reported, not killed.
foreach ($port in ($servers | ForEach-Object { $ports[$_] })) {
    foreach ($c in Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq 'dotnet') { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        elseif ($proc) { throw "port $port is used by $($proc.ProcessName) (pid $($proc.Id)); close it or change the port (Agents/GettingStarted.md)" }
    }
}
Start-Sleep -Milliseconds 500
foreach ($s in $servers) {
    Remove-Item (Join-Path $out "live-$($s.ToLower()).log"), (Join-Path $out "live-$($s.ToLower()).err") -ErrorAction SilentlyContinue
}
if (-not $Only) { Remove-Item (Join-Path $out 'walkmaps.*') -ErrorAction SilentlyContinue }

if (-not $NoBuild) {
    foreach ($s in $servers) {
        dotnet build (Join-Path $repo "src\GameServer.$s") -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: GameServer.$s (run: dotnet build src\GameServer.$s)" }
    }
}

# Long-running processes start through `cmd /c ... > log` rather than Start-Process -RedirectStandardOutput: the
# redirecting form creates them with handle inheritance on, so they would hold this shell's stdout open, and whoever runs
# this script through a pipe (an AI agent's shell tool, `| Select-Object`) would wait until they exit. Started this way
# (ShellExecute) they inherit the environment (the Game_* settings) but no handles.
function Start-Detached([string]$exe, [string[]]$arguments, [string]$log, [string]$err) {
    $line = (@($exe) + $arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    Start-Process cmd.exe -ArgumentList "/c $line 1>`"$log`" 2>`"$err`"" -WorkingDirectory $repo -WindowStyle Hidden
}

$bin = 'bin\Debug\net10.0'
$serverArgs = @{ Auth = @('serve'); Patch = @(); Gateway = @() }
# The cached gateway list can point elsewhere even when the patch welcome says no download is needed.
# Select the local Auth explicitly before the patch server or client opens the GGPK.
if (-not $NoClient) {
    Select-LocalGateway
}

foreach ($s in 'Patch', 'Gateway', 'Auth') {
    if ($servers -notcontains $s) { continue }
    Start-Detached 'dotnet' (@("src\GameServer.$s\$bin\GameServer.$s.dll") + $serverArgs[$s]) `
        (Join-Path $out "live-$($s.ToLower()).log") (Join-Path $out "live-$($s.ToLower()).err")
}

# The patch server must read the GGPK root hash before the client locks the file.
if ($servers -contains 'Patch') {
    $deadline = (Get-Date).AddSeconds(30)
    # The log appears a moment after the server starts: until then there is nothing to read (with $ErrorActionPreference
    # Stop, Select-String on a missing file would end the script even with -ErrorAction SilentlyContinue).
    $patchLog = Join-Path $out 'live-patch.log'
    while (-not ((Test-Path $patchLog) -and (Select-String -Path $patchLog -Pattern 'patching protocol on' -Quiet))) {
        if ((Get-Date) -gt $deadline) {
            $err = Get-Content (Join-Path $out 'live-patch.err') -Raw -ErrorAction SilentlyContinue
            throw "patch server did not start (logs\live-patch.err):`n$err"
        }
        Start-Sleep -Milliseconds 300
    }
}
Start-Sleep -Seconds 1
foreach ($name in ($servers | Where-Object { $_ -ne 'Patch' } | ForEach-Object { $_.ToLower() })) {
    $err = Get-Content (Join-Path $out "live-$name.err") -Raw -ErrorAction SilentlyContinue
    if ($err) { throw "$name failed to start (logs\live-$name.err):`n$err" }
}

if ($Walkmaps) {
    Start-Detached $python @((Join-Path $repo 'scripts\walkmaps\walkmaps.py')) (Join-Path $out 'walkmaps.log') (Join-Path $out 'walkmaps.err')
    Write-Output 'walk-map capture on (logs\walkmaps.log)'
}

if (-not $NoClient) {
    $client = Join-Path $repo 'Client'
    $p = Start-Process (Join-Path $client 'Game.patched.exe') -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory $client -PassThru
    Write-Output "client pid $($p.Id)"
}
Write-Output $(if ($Only) { "restarted: $($Only -join ', ')" } else { 'servers up' })
