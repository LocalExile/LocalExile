# One run against the official client (see Agents/GettingStarted.md):
#   checks the one-time setup, stops this checkout's servers and patched client, builds Auth, Patch and Gateway,
#   starts all three (logs in logs/live-*.log) and launches Client/Game.patched.exe through the local patching server.
#   The previous run's logs move to logs/archive/<time>/ (the newest 10 full runs are kept), and the Gateway writes each
#   connection's last packets to logs/packets/ when it closes (Game_PACKET_DUMPS; scripts/dev/bugreport.ps1 bundles both).
#   -NoClient   keep a running client (it drops to the login screen; log in again)
#   -NoBuild    skip the builds
#   -NoExtract  skip scripts/dev/extract-data.ps1, which otherwise first extracts any missing or outdated game data into
#               data/ (a quick check when nothing is missing)
#   -Walkmaps   also run scripts/walkmaps/walkmaps.py (needs Python + `pip install frida`): captures the walk map of every
#               area the client generates into data/walkability/, and lets the Gateway announce each load to it and run
#               capture tours (Game_WALKMAPS=1). Without it nothing is recorded. Default ports and client session only.
#   -Only Gateway[,Auth,Patch]   rebuild and restart only these servers; the others and the client keep running (implies
#               -NoClient). Restarting the Gateway drops the client to the login screen, but Auth keeps the login session.
#   The Gateway runs under `dotnet watch` (.NET hot reload): a saved edit to its code applies to the running server
#               within seconds and the client stays in game. An edit hot reload cannot apply (a changed signature, a new
#               static initializer) rebuilds and restarts the Gateway, which drops the client to the login screen.
#   -NoHotReload  start the built Gateway without `dotnet watch`. Agents/GettingStarted.md step 10.
#   -ClientOnly open the client (of -Session) when it is closed; the servers must be running. Nothing else is touched.
#   -PortBase N run this checkout's stack on ports N to N+4 (Patch, Patch web, Auth login, Auth control, Gateway) instead
#               of 12995, 8080, 20493, 20495 and 6112, beside another checkout's stack. Game_PORT_BASE does the same. The
#               base is remembered in data\stack.json for later runs and scripts/dev/client_control.py; 0 = default ports.
#   -Session S  a named client (also Game_CLIENT_SESSION) beside the others of this stack: its own working folder
#               data\clients\S\ (its logs\Client.txt), settings file and saved login. Without it: the one client as before.
#   -Stop       stop this checkout's servers, walk-map recorder and clients; with -ClientOnly only the -Session client.
#   Several stacks and clients on one PC: Agents/docs/dev/live-testing.md ("Several stacks and clients"). Whatever this
#   script stops is this checkout's own (its paths, or the client PIDs it recorded in data\clients\), never another's.
param([switch]$NoClient, [switch]$NoBuild, [switch]$NoExtract, [switch]$Walkmaps, [switch]$ClientOnly, [switch]$NoHotReload,
    [ValidateSet('Auth', 'Patch', 'Gateway')][string[]]$Only = @(), [int]$PortBase = -1, [string]$Session = $env:Game_CLIENT_SESSION,
    [switch]$Stop)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'logs'
$clients = Join-Path $repo 'data\clients'
New-Item -ItemType Directory -Force $out | Out-Null

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }

# This checkout's ports. Without a base, today's; with one, five in a row from it. A base given here or in Game_PORT_BASE
# is written to data\stack.json (0 removes it), which later runs and the control bridge read. A server's own variable
# (Game_LOGIN_PORT, ...) still wins over the base.
function Get-Stack([int]$base) {
    $file = Join-Path $repo 'data\stack.json'
    $given = $base -ge 0
    if (-not $given -and "$env:Game_PORT_BASE" -ne '') { $base = [int]$env:Game_PORT_BASE; $given = $true }
    if (-not $given) { $base = if (Test-Path $file) { [int](Get-Content $file -Raw | ConvertFrom-Json).portBase } else { 0 } }
    if ($base -ne 0 -and ($base -lt 1024 -or $base -gt 65530)) { throw "port base $base is outside 1024-65530 (0 = the default ports)" }
    function Pick([string]$name, [int]$offset, [int]$default) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { [int]$value } elseif ($base) { $base + $offset } else { $default }
    }
    $web = if ($env:Game_PATCH_URL -match ':(\d+)') { [int]$Matches[1] } elseif ($base) { $base + 1 } else { 8080 }
    $ports = [ordered]@{ patch = (Pick 'Game_PATCH_TCP_PORT' 0 12995); patchWeb = $web; login = (Pick 'Game_LOGIN_PORT' 2 20493)
        control = (Pick 'Game_AUTH_CONTROL_PORT' 3 20495); gateway = (Pick 'Game_GATEWAY_PORT' 4 6112) }
    if ($given -and $base) { [ordered]@{ portBase = $base; ports = $ports } | ConvertTo-Json | Set-Content $file -Encoding ascii }
    elseif ($given -and (Test-Path $file)) { Remove-Item $file }
    [pscustomobject]@{ Base = $base; Ports = $ports }
}

# '' and 'default' are the client as before (started in Client\ with the shared settings); other names are sessions.
function Get-SessionName([string]$name) {
    if (-not $name -or $name -eq 'default') { return 'default' }
    if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,23}$') { throw "session name '$name': up to 24 letters, digits, - and _" }
    $name
}

# Whether a process (Win32_Process) names a path inside this checkout on its command line or as its executable.
function Test-Here($process) {
    $null -ne $process -and "$($process.CommandLine)|$($process.ExecutablePath)".IndexOf("$repo\", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

# This checkout's patched clients. Each checkout reaches the shared client through its own Client\ path, which tells
# checkouts apart; the PID records Start-Client writes (data\clients\<session>.json) tell its sessions apart. A client
# no record names (one an older live.ps1 started) is the default session's. Without a session: all of them.
function Get-OwnClients([string]$session) {
    $exe = Join-Path $repo 'Client\Game.patched.exe'
    $mine = @(Get-CimInstance Win32_Process -Filter "Name = 'Game.patched.exe'" -ErrorAction SilentlyContinue |
        Where-Object { [string]::Equals("$($_.ExecutablePath)", $exe, [StringComparison]::OrdinalIgnoreCase) })
    if (-not $session) { return $mine }
    $records = @{}
    foreach ($file in Get-ChildItem $clients -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        try { $records[$file.BaseName] = Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { }
    }
    # The PID and its start time: a later process may reuse the PID.
    $recorded = { param($p, $r) $p.ProcessId -eq $r.pid -and [math]::Abs($p.CreationDate.ToFileTimeUtc() - [long]$r.started) -lt 20000000 }
    if ($records.ContainsKey($session)) { $r = $records[$session]; return @($mine | Where-Object { & $recorded $_ $r }) }
    if ($session -ne 'default') { return @() }
    @($mine | Where-Object { $p = $_; -not @($records.Values | Where-Object { & $recorded $p $_ }).Count })
}

# This checkout's processes of the given servers: `dotnet <server>.dll`, a watched Gateway's `dotnet watch`, and their cmd
# wrappers, found by their command line (one still loading its game data holds no port yet, and its cmd wrapper keeps the
# log open), with everything they started (`dotnet watch` runs `dotnet run`, which runs GameServer.Gateway.exe). They
# name a path inside this checkout themselves or through their parent (an older live.ps1 started them with relative
# paths, but its cmd wrapper names its log files here). Builds, test runs and other checkouts' servers are left alone.
function Get-OwnServers([string[]]$names) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $byId = @{}
    foreach ($p in $all) { $byId[[int]$p.ProcessId] = $p }
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($p in $all) {
        $line = "$($p.CommandLine)"
        foreach ($s in $names) {
            $server = $p.Name -eq "GameServer.$s.exe" -or ($p.Name -in 'dotnet.exe', 'cmd.exe' -and $line -like "*GameServer.$s*" -and
                $line -notmatch "GameServer\.$s\.Tests|\s(build|test)\s" -and ($line -like "*GameServer.$s.dll*" -or $line -match '\bwatch\b|DOTNET_WATCH'))
            if ($server -and ((Test-Here $p) -or (Test-Here $byId[[int]$p.ParentProcessId]))) { $queue.Enqueue($p); break }
        }
    }
    $found = @{}
    while ($queue.Count) {
        $p = $queue.Dequeue()
        if ($found.ContainsKey([int]$p.ProcessId)) { continue }
        $found[[int]$p.ProcessId] = $p
        # Children started after their parent (an older process may hold a reused parent PID).
        foreach ($c in $all) { if ($c.ParentProcessId -eq $p.ProcessId -and $c.CreationDate -ge $p.CreationDate) { $queue.Enqueue($c) } }
    }
    @($found.Values)
}

function Get-OwnWalkmaps {
    @(Get-CimInstance Win32_Process -Filter "Name like 'python%'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*walkmaps.py*' -and (Test-Here $_) })
}

function Stop-Processes($processes) {
    foreach ($p in $processes) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
}

# The client's own Log In goes to the login server of the GGPK's cached gateway list; the control bridge
# (client_control.py) connects to this stack's Auth by port and needs no list. The GGPK is shared by every checkout and
# cannot be written while any client runs, so only the default stack points it at its Auth, and only when the list
# (read first) does not already.
function Select-LocalGateway([int]$loginPort) {
    $patch = Join-Path $repo 'src\GameServer.Patch\bin\Debug\net10.0\GameServer.Patch.dll'
    $ggpk = Join-Path $repo 'Client\Content.ggpk'
    $list = @(& dotnet $patch gateway $ggpk | Where-Object { "$_" -match '^[^;]*;[^;]*;' })
    if ($LASTEXITCODE -eq 0 -and $list.Count -and -not @($list | Where-Object { "$_" -notmatch "^[^;]*;[^;]*;127\.0\.0\.1:$loginPort;" }).Count) { return }
    & dotnet $patch gateway $ggpk --login "127.0.0.1:$loginPort" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'could not select local login server; close all clients and retry' }
}

# Starts the session's client through this stack's patching server and records its PID. A named session runs in its own
# folder, data\clients\<session>\: the client opens Content.ggpk, config.json and logs\Client.txt in its working folder,
# so the folder gets a hard link to the shared Content.ggpk (the same file: nothing is copied or written) and a
# config.json naming the session's own settings file and saved login (GGG_CONFIG_FILE_NAME, GGG_CREDENTIAL_FILE_NAME,
# in Documents\My Games\Path of Exile). A new settings file starts as a copy of the default client's without its [LOGIN]
# section: no saved login to send to whichever login server the shared gateway list names.
function Start-Client([string]$session, $stack) {
    $client = Join-Path $repo 'Client'
    $folder = $client
    if ($session -ne 'default') {
        $folder = Join-Path $clients $session
        New-Item -ItemType Directory -Force $folder | Out-Null
        $shared = Join-Path $client 'Content.ggpk'
        $link = Join-Path $folder 'Content.ggpk'
        if (Test-Path $link) {
            # A link to a GGPK that has been replaced since: only this folder's link goes.
            $a = Get-Item $link; $b = Get-Item $shared
            if ($a.Length -ne $b.Length -or $a.LastWriteTimeUtc -ne $b.LastWriteTimeUtc) { Remove-Item $link -Force }
        }
        if (-not (Test-Path $link)) { New-Item -ItemType HardLink -Path $link -Target $shared | Out-Null }
        $prefix = "localexile-$(Split-Path $repo -Leaf)-$session"
        [ordered]@{ GGG_CONFIG_FILE_NAME = "${prefix}_Config.ini"; GGG_CREDENTIAL_FILE_NAME = "${prefix}_login.credentials" } |
            ConvertTo-Json | Set-Content (Join-Path $folder 'config.json') -Encoding ascii
        $settings = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Path of Exile'
        $ini = Join-Path $settings "${prefix}_Config.ini"
        $seed = Join-Path $settings 'poeoffline_Config.ini'
        if (-not (Test-Path $ini) -and (Test-Path $seed)) {
            $section = $null
            $lines = foreach ($line in [IO.File]::ReadAllLines($seed)) {
                if ($line -match '^\[(.+)\]\s*$') { $section = $Matches[1] }
                if ($section -ne 'LOGIN') { $line }
            }
            [IO.File]::WriteAllLines($ini, [string[]]$lines, [Text.UTF8Encoding]::new($true))
        }
    }
    $p = Start-Process (Join-Path $client 'Game.patched.exe') -ArgumentList '--patching-server', "127.0.0.1:$($stack.Ports.patch)" -WorkingDirectory $folder -PassThru
    New-Item -ItemType Directory -Force $clients | Out-Null
    [ordered]@{ pid = $p.Id; started = $p.StartTime.ToFileTimeUtc(); session = $session; folder = $folder; ports = $stack.Ports } |
        ConvertTo-Json | Set-Content (Join-Path $clients "$session.json") -Encoding ascii
    Write-Output "client pid $($p.Id)$(if ($session -ne 'default') { " (session $session, $folder)" })"
}

$stack = Get-Stack $PortBase
$session = Get-SessionName $Session
$ports = @{ Auth = @($stack.Ports.login, $stack.Ports.control); Patch = @($stack.Ports.patch, $stack.Ports.patchWeb); Gateway = @($stack.Ports.gateway) }

if ($Stop) {
    $stopping = if ($ClientOnly) { @(Get-OwnClients $session) } else { @(Get-OwnClients) + @(Get-OwnWalkmaps) + @(Get-OwnServers 'Auth', 'Patch', 'Gateway') }
    Stop-Processes $stopping
    if ($ClientOnly) { Remove-Item (Join-Path $clients "$session.json") -ErrorAction SilentlyContinue }
    else { Remove-Item (Join-Path $clients '*.json') -ErrorAction SilentlyContinue }
    Write-Output "stopped $(@($stopping).Count) process(es) of $repo"
    return
}

Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Test-Path (Join-Path $repo 'data\login-keys.json')) 'data\login-keys.json missing: run GameServer.Auth keygen'
Need (Test-Path (Join-Path $repo 'Client\Game.exe')) 'Client\Game.exe missing: copy the client and its PathOfExile.exe'
Need (Test-Path (Join-Path $repo 'Client\Game.patched.exe')) 'Client\Game.patched.exe missing: run GameServer.Auth patch'
Need (Test-Path (Join-Path $repo 'Client\Content.ggpk')) 'Client\Content.ggpk missing: this needs the standalone client layout'

# The control bridge can open a closed client without restarting the servers or touching any other client.
if ($ClientOnly) {
    if ($NoClient -or $Only -or $Walkmaps) { throw '-ClientOnly cannot be combined with -NoClient, -Only or -Walkmaps' }
    $existing = @(Get-OwnClients $session)
    if ($existing.Count) {
        Write-Output "client already running: $($existing[0].ProcessId)"
        return
    }
    foreach ($port in @($stack.Ports.patch, $stack.Ports.login, $stack.Ports.gateway)) {
        Need (@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) "local servers must be running before -ClientOnly (missing port $port)"
    }
    if (-not $stack.Base) { Select-LocalGateway $stack.Ports.login }
    Start-Client $session $stack
    return
}

$python = $null
if ($Walkmaps) {
    # The recorder attaches to the client by name and meets the Gateway in the shared data\walkability\current.json.
    if ($stack.Base -or $session -ne 'default') { throw '-Walkmaps records only with the default ports and client session (data\walkability\current.json is shared)' }
    $foreign = @(Get-CimInstance Win32_Process -Filter "Name = 'Game.patched.exe'" -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::Equals("$($_.ExecutablePath)", (Join-Path $repo 'Client\Game.patched.exe'), [StringComparison]::OrdinalIgnoreCase) })
    if ($foreign.Count) { throw "-Walkmaps attaches to the client by name, and another checkout's client runs (pid $($foreign[0].ProcessId))" }
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
# Hot reload (the Gateway under `dotnet watch`) unless -NoHotReload.
$Watch = -not $NoHotReload -and $servers -contains 'Gateway'

# This checkout's clients (every session: they lose their servers) and recorder.
if (-not $NoClient) {
    Stop-Processes (Get-OwnClients)
    Remove-Item (Join-Path $clients '*.json') -ErrorAction SilentlyContinue
}
if (-not $Only) { Stop-Processes (Get-OwnWalkmaps) }
# This checkout's servers go first, then whatever of this checkout still listens on the stack's ports. Anything else
# there (another checkout's stack on the same ports, another program) is reported, not stopped.
Stop-Processes (Get-OwnServers $servers)
foreach ($port in ($servers | ForEach-Object { $ports[$_] })) {
    foreach ($c in Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($c.OwningProcess)" -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.ParentProcessId)" -ErrorAction SilentlyContinue
        if ($proc.Name -in 'dotnet.exe', 'GameServer.Gateway.exe' -and ((Test-Here $proc) -or (Test-Here $parent))) {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        }
        else {
            throw ("port $port is used by $($proc.Name) (pid $($proc.ProcessId)) $($proc.ExecutablePath), not this checkout's server: " +
                'close it, or give this checkout its own ports with -PortBase (Agents/docs/dev/live-testing.md)')
        }
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
    # A watched Gateway builds itself.
    foreach ($s in ($servers | Where-Object { -not ($Watch -and $_ -eq 'Gateway') })) {
        dotnet build (Join-Path $repo "src\GameServer.$s") -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: GameServer.$s (run: dotnet build src\GameServer.$s)" }
    }
    # The first-party mods (Mods\Source\) into Mods\Binaries\, which the Gateway loads (Agents/docs/dev/modding.md). One that does
    # not build is left out; the Gateway runs without it.
    if ($servers -contains 'Gateway') {
        foreach ($mod in Get-ChildItem (Join-Path $repo 'Mods\Source') -Filter *.csproj -Recurse -ErrorAction SilentlyContinue) {
            dotnet build $mod.FullName -v q -nologo | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "mod $($mod.BaseName) did not build (run: dotnet build $($mod.FullName)); the Gateway starts without it" }
        }
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
# The servers inherit it: the Gateway records area loads for the recorder only beside it (AreaLayouts.Recording).
$env:Game_WALKMAPS = if ($Walkmaps) { '1' } else { $null }
# The cached gateway list can point elsewhere even when the patch welcome says no download is needed.
# Select the local Auth explicitly before the patch server or client opens the GGPK (the default stack only).
if (-not $NoClient -and -not $stack.Base) {
    Select-LocalGateway $stack.Ports.login
}

# Only for the servers (the shell keeps its own values): this stack's ports; each connection's last packets go to a file
# when it closes (PacketHistory, the lead for a client crash; relative to the servers' working directory, this checkout;
# 0 turns it off); a watched Gateway reads `dotnet watch`'s own output (its stderr, below) to tell players about compile
# errors (HotReload.cs).
$serverEnv = @{
    Game_PATCH_TCP_PORT = "$($stack.Ports.patch)"; Game_LOGIN_PORT = "$($stack.Ports.login)"
    Game_AUTH_CONTROL_PORT = "$($stack.Ports.control)"; Game_GATEWAY_PORT = "$($stack.Ports.gateway)"
    Game_PATCH_URL = if ($env:Game_PATCH_URL) { $env:Game_PATCH_URL } else { "http://127.0.0.1:$($stack.Ports.patchWeb)" }
    Game_PACKET_DUMPS = if ($env:Game_PACKET_DUMPS) { $env:Game_PACKET_DUMPS } else { 'logs\packets' }
    Game_HOT_RELOAD_LOG = if ($Watch) { Join-Path $out 'live-gateway.err' } else { $null }
}
$envBefore = @{}
foreach ($name in $serverEnv.Keys) {
    $envBefore[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $serverEnv[$name])
}
try {
    foreach ($s in 'Patch', 'Gateway', 'Auth') {
        if ($servers -notcontains $s) { continue }
        # `dotnet watch` runs the Gateway with this checkout as its working directory, like the plain start, so data\
        # and logs\ resolve the same. --non-interactive restarts it on an edit hot reload cannot apply instead of asking.
        # Absolute paths: the command line tells this checkout's servers from another checkout's (Get-OwnServers).
        # @(...): a single value would come out as a string, and `+` would glue 'serve' onto the dll's name.
        $command = @(if ($Watch -and $s -eq 'Gateway') { @('watch', 'run', '--project', (Join-Path $repo "src\GameServer.$s"), '--non-interactive', '--') }
            else { Join-Path $repo "src\GameServer.$s\$bin\GameServer.$s.dll" })
        Start-Detached 'dotnet' ($command + $serverArgs[$s]) `
            (Join-Path $out "live-$($s.ToLower()).log") (Join-Path $out "live-$($s.ToLower()).err")
    }
}
finally { foreach ($name in $envBefore.Keys) { [Environment]::SetEnvironmentVariable($name, $envBefore[$name]) } }

# A watched Gateway first builds: wait until it listens, so the client does not reach a closed port.
if ($Watch) {
    $deadline = (Get-Date).AddMinutes(3)
    $gatewayLog = Join-Path $out 'live-gateway.log'
    $gatewayLogs = @($gatewayLog, (Join-Path $out 'live-gateway.err'))
    while (-not ((Test-Path $gatewayLog) -and (Select-String -Path $gatewayLog -Pattern '\[gateway\] listening on' -Quiet))) {
        if ((Get-Date) -gt $deadline) { throw 'the watched Gateway did not start (logs\live-gateway.log, logs\live-gateway.err)' }
        $built = @($gatewayLogs | Where-Object { Test-Path $_ })
        if ($built -and (Select-String -Path $built -Pattern 'Build failed|error CS' -Quiet)) {
            throw 'the watched Gateway did not build (logs\live-gateway.log, logs\live-gateway.err)'
        }
        Start-Sleep -Milliseconds 500
    }
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
    # `dotnet watch` writes its own status lines (hot reload applied, file updated) to stderr.
    $err = (Get-Content (Join-Path $out "live-$name.err") -ErrorAction SilentlyContinue |
        Where-Object { -not ($Watch -and $name -eq 'gateway' -and $_ -match '^dotnet watch ') }) -join "`n"
    if ($err) { throw "$name failed to start (logs\live-$name.err):`n$err" }
}

if ($Walkmaps) {
    Start-Detached $python @((Join-Path $repo 'scripts\walkmaps\walkmaps.py')) (Join-Path $out 'walkmaps.log') (Join-Path $out 'walkmaps.err')
    Write-Output 'walk-map capture on (logs\walkmaps.log)'
}

if (-not $NoClient) { Start-Client $session $stack }
$portsText = if ($stack.Base) { " on ports $(($stack.Ports.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')" } else { '' }
Write-Output $(if ($Only) { "restarted: $($Only -join ', ')$portsText" } else { "servers up$portsText" })
