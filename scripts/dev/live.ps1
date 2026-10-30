# One run against the official client (see Agents/GettingStarted.md):
#   checks the one-time setup, stops anything on the dev ports and the patched client, builds Auth, Patch and Gateway,
#   starts all three (logs in logs/live-*.log) and launches Client/Game.patched.exe through the local patching server.
#   The previous run's logs move to logs/archive/<time>/ (the newest 10 full runs are kept), and the Gateway writes each
#   connection's last packets to logs/packets/ when it closes (Game_PACKET_DUMPS; scripts/dev/bugreport.ps1 bundles both).
#   -NoClient   keep a running client (it drops to the login screen; log in again)
#   -NoBuild    skip the builds
#   -NoExtract  skip scripts/dev/extract-data.ps1, which otherwise first extracts any missing or outdated game data into
#               data/ (a quick check when nothing is missing)
#   -Walkmaps   also run scripts/walkmaps/walkmaps.py (needs Python + `pip install frida`): captures the walk map of every
#               area the client generates into data/walkability/, and lets the Gateway announce each load to it and run
#               capture tours (Game_WALKMAPS=1). Without it nothing is recorded.
#   -Only Gateway[,Auth,Patch]   rebuild and restart only these servers; the others and the client keep running (implies
#               -NoClient). Restarting the Gateway drops the client to the login screen, but Auth keeps the login session.
param([switch]$NoClient, [switch]$NoBuild, [switch]$NoExtract, [switch]$Walkmaps, [switch]$ClientOnly, [ValidateSet('Auth', 'Patch', 'Gateway')][string[]]$Only = @())

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'client-path.ps1')
$clientPaths = Get-ClientPaths -RepositoryRoot $repo
$buildDirectory = Get-BuildDirectory $repo
$runtime = Get-RuntimeDirectory $repo
& (Join-Path $PSScriptRoot 'setup-runtime.ps1')
$client = $clientPaths.Directory
$savedClientEnv = @{ Game_CLIENT_EXE = $env:Game_CLIENT_EXE; Game_GGPK = $env:Game_GGPK
    Game_SOURCE_ROOT = $env:Game_SOURCE_ROOT; Game_RUNTIME_DIR = $env:Game_RUNTIME_DIR }
try {
$env:Game_CLIENT_EXE = $clientPaths.Executable
$env:Game_GGPK = $clientPaths.Archive
$env:Game_SOURCE_ROOT = $repo
$env:Game_RUNTIME_DIR = $runtime
$out = Join-Path $runtime 'logs'
New-Item -ItemType Directory -Force $out | Out-Null

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }
function Server-Assembly([string]$Name) {
    Get-ProjectAssembly $repo "src\GameServer.$Name" $buildDirectory
}
function Need-ServerAssembly([string]$Name) {
    $assembly = Server-Assembly $Name
    Need (Test-Path -LiteralPath $assembly -PathType Leaf) "$assembly missing: run live.ps1 without -NoBuild first"
}
function Select-LocalGateway {
    # The client can use a cached gateway list even when the patch welcome needs no download.
    Need-ServerAssembly 'Patch'
    Push-Location $runtime
    try { & dotnet (Server-Assembly 'Patch') gateway $clientPaths.Archive --login '127.0.0.1:20493' | Out-Null }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'could not select local login server; close all clients and retry' }
}
Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Test-Path (Join-Path $runtime 'data\login-keys.json')) "$runtime\data\login-keys.json missing: see runtime setup in GettingStarted"
Need (Test-Path $clientPaths.Executable) "$($clientPaths.Executable) missing: copy the client and its PathOfExile.exe"
Need (Test-Path $clientPaths.PatchedExecutable) "$($clientPaths.PatchedExecutable) missing: run GameServer.Auth patch"
Need (Test-Path $clientPaths.Archive) "$($clientPaths.Archive) missing: this needs the standalone client layout"

# The control bridge can open a closed client without restarting the servers or touching an existing game.
if ($ClientOnly) {
    if ($NoClient -or $Only -or $Walkmaps) { throw '-ClientOnly cannot be combined with -NoClient, -Only or -Walkmaps' }
    $existing = @(Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue)
    if ($existing.Count) {
        Need ($existing.Count -eq 1 -and $existing[0].Path -eq $clientPaths.PatchedExecutable) 'another patched client is running'
        Write-Output "client already running: $($existing[0].Id)"
        return
    }
    foreach ($port in @(12995, 20493, 6112)) {
        Need (@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) "local servers must be running before -ClientOnly (missing port $port)"
    }
    Select-LocalGateway
    $p = Start-Process $clientPaths.PatchedExecutable -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory $client -PassThru
    Write-Output "client pid $($p.Id)"
    return
}

$servers = if ($Only) { $Only } else { @('Auth', 'Patch', 'Gateway') }
if ($Only) { $NoClient = $true }
# Fail before extraction or stopping a session when the selected layout has not been built.
if ($NoBuild) { foreach ($s in $servers) { Need-ServerAssembly $s } }

$python = $null
if ($Walkmaps) {
    & (Join-Path $PSScriptRoot 'setup-runtime.ps1') -PythonTools
    # Native helpers share a managed environment in the selected runtime directory.
    $venv = Join-Path $runtime '.venv\Scripts\python.exe'
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

$patchWebPort = if ($env:Game_PATCH_URL -match ':(\d+)') { [int]$Matches[1] } else { 8080 }
$ports = @{ Auth = @(20493); Patch = @(12995, $patchWebPort); Gateway = @(6112) }

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
# The previous run's logs move to logs\archive\<time>\ instead of being deleted: a crash noticed only after the
# restart still has its log. Empty files go. A -Only restart's folder is <time>-<servers>; the newest 10 full runs and
# the newest 20 restarts are kept, so restarting the Gateway while debugging does not push the full runs out.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($Only) { $stamp += '-' + (($Only | ForEach-Object { $_.ToLower() }) -join '-') }
$archive = Join-Path (Join-Path $out 'archive') $stamp
$previous = @($servers | ForEach-Object { "live-$($_.ToLower()).log", "live-$($_.ToLower()).err" })
if (-not $Only) { $previous += 'walkmaps.log', 'walkmaps.err' }
foreach ($name in $previous) {
    $path = Join-Path $out $name
    if (-not (Test-Path $path)) { continue }
    if ((Get-Item $path).Length -gt 0) {
        New-Item -ItemType Directory -Force $archive | Out-Null
        Move-Item $path $archive -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $path -ErrorAction SilentlyContinue
}
$runs = @(Get-ChildItem (Join-Path $out 'archive') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
@($runs | Where-Object { $_.Name -match '^\d{8}-\d{6}$' } | Select-Object -Skip 10) +
    @($runs | Where-Object { $_.Name -notmatch '^\d{8}-\d{6}$' } | Select-Object -Skip 20) |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

if (-not $NoBuild) {
    foreach ($s in $servers) {
        $buildArgs = @(Get-DotnetBuildArguments (Join-Path $repo "src\GameServer.$s") $buildDirectory) + @('-v', 'q', '-nologo')
        $buildOutput = & dotnet @buildArgs 2>&1
        if ($LASTEXITCODE -ne 0) { throw "build failed: GameServer.$s`n$($buildOutput -join "`n")" }
    }
}

# Long-running processes start through `cmd /c ... > log` rather than Start-Process -RedirectStandardOutput: the
# redirecting form creates them with handle inheritance on, so they would hold this shell's stdout open, and whoever runs
# this script through a pipe (an AI agent's shell tool, `| Select-Object`) would wait until they exit. Started this way
# (ShellExecute) they inherit the environment (the Game_* settings) but no handles.
function Start-Detached([string]$exe, [string[]]$arguments, [string]$log, [string]$err) {
    $line = (@($exe) + $arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    Start-Process cmd.exe -ArgumentList "/c $line 1>`"$log`" 2>`"$err`"" -WorkingDirectory $runtime -WindowStyle Hidden
}

$serverArgs = @{ Auth = @('serve'); Patch = @(); Gateway = @() }
# The servers inherit it: the Gateway records area loads for the recorder only beside it (AreaLayouts.Recording).
$env:Game_WALKMAPS = if ($Walkmaps) { '1' } else { $null }
# The cached gateway list can point elsewhere even when the patch welcome says no download is needed.
# Select the local Auth explicitly before the patch server or client opens the GGPK.
if (-not $NoClient) {
    Select-LocalGateway
}

# Each connection's last packets go to a file when it closes (PacketHistory): the lead for a client crash. Relative to
# the servers' working directory (the runtime directory), and only for them: the shell keeps its own value (0 turns it off).
$dumpsBefore = $env:Game_PACKET_DUMPS
if (-not $env:Game_PACKET_DUMPS) { $env:Game_PACKET_DUMPS = 'logs\packets' }
try {
    foreach ($s in 'Patch', 'Gateway', 'Auth') {
        if ($servers -notcontains $s) { continue }
        Need-ServerAssembly $s
        Start-Detached 'dotnet' (@((Server-Assembly $s)) + $serverArgs[$s]) `
            (Join-Path $out "live-$($s.ToLower()).log") (Join-Path $out "live-$($s.ToLower()).err")
    }
}
finally { $env:Game_PACKET_DUMPS = $dumpsBefore }

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
    $p = Start-Process (Join-Path $client 'Game.patched.exe') -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory $client -PassThru
    Write-Output "client pid $($p.Id)"
}
Write-Output $(if ($Only) { "restarted: $($Only -join ', ')" } else { 'servers up' })

} finally {
    foreach ($key in $savedClientEnv.Keys) {
        if ($null -eq $savedClientEnv[$key]) { Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $savedClientEnv[$key]) }
    }
}
