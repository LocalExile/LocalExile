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
#   -Site       first start the website in Site/ (Nuxt, dev mode) on http://127.0.0.1:3000 (needs Node.js); its
#               dependencies are installed on the first run and whenever Site/package-lock.json changes. Logs:
#               logs/site-install.log, logs/live-site.log. Ignored with -Only (the running site is left alone).
param([switch]$NoClient, [switch]$NoBuild, [switch]$NoExtract, [switch]$Walkmaps, [switch]$Site, [ValidateSet('Auth', 'Patch', 'Gateway')][string[]]$Only = @())

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force $out | Out-Null

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }
Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Test-Path (Join-Path $repo 'data\login-keys.json')) 'data\login-keys.json missing: run GameServer.Auth keygen'
Need (Test-Path (Join-Path $repo 'Client\Game.exe')) 'Client\Game.exe missing: copy the client and its PathOfExile.exe'
Need (Test-Path (Join-Path $repo 'Client\Game.patched.exe')) 'Client\Game.patched.exe missing: run GameServer.Auth patch'
Need (Test-Path (Join-Path $repo 'Client\Content.ggpk')) 'Client\Content.ggpk missing: this needs the standalone client layout'

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

# Long-running processes start through `cmd /c ... > log` rather than Start-Process -RedirectStandardOutput: the
# redirecting form creates them with handle inheritance on, so they would hold this shell's stdout open, and whoever runs
# this script through a pipe (an AI agent's shell tool, `| Select-Object`) would wait until they exit. Started this way
# (ShellExecute) they inherit the environment (the Game_* settings) but no handles.
function Start-Detached([string]$exe, [string[]]$arguments, [string]$log, [string]$err, [string]$dir = $repo) {
    $line = (@($exe) + $arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    Start-Process cmd.exe -ArgumentList "/c $line 1>`"$log`" 2>`"$err`"" -WorkingDirectory $dir -WindowStyle Hidden
}

# The website (Site/) comes up before the servers and the client.
if ($Site -and -not $Only) {
    $siteDir = Join-Path $repo 'Site'
    $sitePort = 3000
    Need (Test-Path (Join-Path $siteDir 'package.json')) 'Site\package.json missing'
    Need (Get-Command npm.cmd -ErrorAction SilentlyContinue) 'Node.js not found (npm, needed for -Site)'
    # By name from PATH, like dotnet: a quoted full path ("C:\Program Files\...") would break the cmd /c line.
    $npm = 'npm.cmd'

    # npm writes node_modules/.package-lock.json on every install: missing or older than package-lock.json means the
    # dependencies are not installed yet or are out of date.
    $lock = Join-Path $siteDir 'package-lock.json'
    $installed = Join-Path $siteDir 'node_modules\.package-lock.json'
    if (-not (Test-Path $installed) -or ((Test-Path $lock) -and (Get-Item $lock).LastWriteTime -gt (Get-Item $installed).LastWriteTime)) {
        Write-Output 'site installing components'
        $install = Start-Process cmd.exe -ArgumentList "/c $npm install --no-audit --no-fund 1>`"$out\site-install.log`" 2>&1" `
            -WorkingDirectory $siteDir -WindowStyle Hidden -Wait -PassThru
        if ($install.ExitCode -ne 0) { throw "site install failed (logs\site-install.log)" }
    }

    # Stop only our own site (node processes running from Site/); anything else on the port is reported, not killed.
    Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$siteDir\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    foreach ($c in Get-NetTCPConnection -LocalPort $sitePort -State Listen -ErrorAction SilentlyContinue) {
        $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        if ($proc) { throw "port $sitePort is used by $($proc.ProcessName) (pid $($proc.Id)); close it to start the site" }
    }

    Remove-Item (Join-Path $out 'live-site.log'), (Join-Path $out 'live-site.err') -ErrorAction SilentlyContinue
    Start-Detached $npm @('run', 'dev') (Join-Path $out 'live-site.log') (Join-Path $out 'live-site.err') $siteDir
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Get-NetTCPConnection -LocalPort $sitePort -State Listen -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) {
            $tail = (Get-Content (Join-Path $out 'live-site.err'), (Join-Path $out 'live-site.log') -Tail 15 -ErrorAction SilentlyContinue) -join "`n"
            throw "site did not start on port $sitePort (logs\live-site.log, logs\live-site.err):`n$tail"
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Output "site running on http://127.0.0.1:$sitePort"
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

$bin = 'bin\Debug\net10.0'
$serverArgs = @{ Auth = @('serve'); Patch = @(); Gateway = @() }
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
