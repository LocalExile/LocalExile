# Exercise live.ps1's client launch, port and process-ownership logic with mocks: no client, server, or GGPK is touched.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'live.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('Need', 'Get-Stack', 'Get-SessionName', 'Test-Here', 'Get-OwnClients', 'Get-OwnServers', 'Select-LocalGateway')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if (-not $definition) { throw "missing function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$branch = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$ClientOnly' -and $node.Extent.Text -like '*Start-Client*'
}, $false)
if (-not $branch) { throw 'missing ClientOnly branch' }
$launch = [scriptblock]::Create($branch.Extent.Text)

$repo = Join-Path ([IO.Path]::GetTempPath()) "localexile-live-test-$PID"
$clients = Join-Path $repo 'data\clients'
New-Item -ItemType Directory -Force $clients | Out-Null
$ownExe = Join-Path $repo 'Client\Game.patched.exe'
$ClientOnly = $true
$NoClient = $false
$Only = @()
$Walkmaps = $false
$script:events = [System.Collections.Generic.List[string]]::new()
$script:processes = @()
$script:missingPort = 0
$script:listed = @('Europe;Local (EU);127.0.0.1:1;127.0.0.1;false')
$script:gatewayExit = 0
$script:gatewayArgs = @()
$script:checkedPorts = @()
$script:started = [datetime]'2026-10-30T21:00:00'
function Get-CimInstance { param($ClassName, $Filter, $ErrorAction)
    if ($Filter -match "Name = '(.+)'") { $n = $Matches[1]; return @($script:processes | Where-Object { $_.Name -eq $n }) }
    $script:processes
}
function Get-NetTCPConnection {
    param($LocalPort, $State, $ErrorAction)
    $script:checkedPorts += $LocalPort
    if ($LocalPort -ne $script:missingPort) { [pscustomobject]@{ LocalPort = $LocalPort } }
}
function dotnet {
    $script:gatewayArgs = @($args)
    if ($args -contains '--login') { $script:events.Add('gateway-write'); $global:LASTEXITCODE = $script:gatewayExit; return }
    $script:events.Add('gateway-read')
    $global:LASTEXITCODE = 0
    '[patch] Bundles2/gateway_list.txt:'
    $script:listed
}
function Start-Client($session, $stack) { $script:events.Add("launch:$session") }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Client($id, $path, $started = $script:started) {
    [pscustomobject]@{ Name = 'Game.patched.exe'; ProcessId = $id; ExecutablePath = $path; CreationDate = $started }
}
function Record($session, $id, $started = $script:started) {
    @{ pid = $id; started = $started.ToFileTimeUtc() } | ConvertTo-Json | Set-Content (Join-Path $clients "$session.json")
}
function Run { $script:events.Clear(); $script:checkedPorts = @(); & $launch | Out-Null }
function Expect-Failure($pattern) {
    $message = $null
    $script:events.Clear()
    try { & $launch | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message -like "*$pattern*") "expected '$pattern', got '$message'"
    Assert (-not @($script:events | Where-Object { $_ -like 'launch*' }).Count) 'failed preflight launched a client'
}

try {
    # The default stack and client, as before: the gateway list is pointed at the local Auth, then the client starts.
    $env:Game_PORT_BASE = $null
    $stack = Get-Stack -1
    $session = Get-SessionName ''
    Assert ($stack.Base -eq 0 -and $stack.Ports.login -eq 20493 -and $stack.Ports.gateway -eq 6112 -and $stack.Ports.patch -eq 12995) 'default ports changed'
    Assert (-not (Test-Path (Join-Path $repo 'data\stack.json'))) 'the default stack must not write data\stack.json'
    Run
    Assert (($script:events -join ',') -eq 'gateway-read,gateway-write,launch:default') "gateway must be selected before a closed client launches: $($script:events -join ',')"
    Assert ($script:gatewayArgs[0] -eq (Join-Path $repo 'src\GameServer.Patch\bin\Debug\net10.0\GameServer.Patch.dll')) 'wrong patch executable'
    Assert (($script:gatewayArgs[1..4] -join '|') -eq ('gateway|' + (Join-Path $repo 'Client\Content.ggpk') + '|--login|127.0.0.1:20493')) 'gateway must target this checkout and local Auth'
    Assert (($script:checkedPorts -join ',') -eq '12995,20493,6112') "default ports not checked: $($script:checkedPorts -join ',')"
    # A list already pointing at the local Auth is not written again (the GGPK is shared and may be open).
    $script:listed = @('Europe;Local (EU);127.0.0.1:20493;127.0.0.1;false', 'America;Local (US);127.0.0.1:20493;127.0.0.1;false')
    Run
    Assert (($script:events -join ',') -eq 'gateway-read,launch:default') 'an already local gateway list must not be written'

    # This checkout's client runs: nothing is written or launched. Another checkout's client does not stop a launch.
    $script:processes = @(Client 42 $ownExe)
    Run
    Assert ($script:events.Count -eq 0) 'existing client must return without writes or launch'
    $script:processes = @(Client 43 'C:\Other\Client\Game.patched.exe'), (Client 44 "$repo-b\Client\Game.patched.exe")
    Run
    Assert (($script:events -join ',') -eq 'gateway-read,launch:default') "another checkout's client must not block or be touched"

    # Named sessions: each is known by its record; an unrecorded client of this checkout is the default session's.
    $script:processes = @((Client 42 $ownExe), (Client 45 $ownExe ([datetime]'2026-10-30T21:05:00')))
    Record 'a1' 45 ([datetime]'2026-10-30T21:05:00')
    $session = Get-SessionName 'a1'
    Run
    Assert ($script:events.Count -eq 0) 'a recorded session client must count as running'
    $session = Get-SessionName 'a2'
    Run
    Assert (($script:events -join ',') -eq 'gateway-read,launch:a2') 'a session without a client must launch beside the others'
    $session = Get-SessionName 'default'
    Run
    Assert ($script:events.Count -eq 0) 'the unrecorded client is the default session'
    Assert (@(Get-OwnClients 'default').ProcessId -eq 42) 'default session must not take the recorded a1 client'
    Assert (@(Get-OwnClients).Count -eq 2) 'all sessions of this checkout'
    # A record whose PID now belongs to a later process does not count.
    Record 'a1' 45 ([datetime]'2026-10-30T20:00:00')
    Assert (@(Get-OwnClients 'a1').Count -eq 0) 'a reused PID is not the session client'
    Remove-Item (Join-Path $clients '*.json')
    foreach ($bad in '-x', 'a b', '..\x', ('a' * 25)) {
        $failed = $false; try { Get-SessionName $bad | Out-Null } catch { $failed = $true }
        Assert $failed "session name '$bad' must be refused"
    }

    # Its own ports: remembered, checked, and the shared GGPK is never written.
    $script:processes = @()
    $session = Get-SessionName ''
    $stack = Get-Stack 21000
    Assert (($stack.Ports.Values -join ',') -eq '21000,21001,21002,21003,21004') "port base order: $($stack.Ports.Values -join ',')"
    Assert ((Get-Content (Join-Path $repo 'data\stack.json') -Raw | ConvertFrom-Json).ports.login -eq 21002) 'stack.json must remember the ports'
    Assert ((Get-Stack -1).Base -eq 21000) 'a later run must reuse the remembered base'
    $env:Game_LOGIN_PORT = '30000'
    Assert ((Get-Stack -1).Ports.login -eq 30000) "a server's own variable wins over the base"
    $env:Game_LOGIN_PORT = $null
    $stack = Get-Stack -1
    Run
    Assert (($script:events -join ',') -eq 'launch:default') "a stack on its own ports must not touch the shared GGPK: $($script:events -join ',')"
    Assert (($script:checkedPorts -join ',') -eq '21000,21002,21004') "the stack's own ports must be checked: $($script:checkedPorts -join ',')"
    $script:missingPort = 21004
    Expect-Failure 'missing port 21004'
    $script:missingPort = 0
    $failed = $false; try { Get-Stack 80 | Out-Null } catch { $failed = $true }
    Assert $failed 'a port base below 1024 must be refused'
    Assert ((Get-Stack 0).Base -eq 0 -and -not (Test-Path (Join-Path $repo 'data\stack.json'))) '-PortBase 0 must forget the base'

    # Failures before any write or launch.
    $stack = Get-Stack -1
    $script:listed = @('Europe;Local (EU);127.0.0.1:1;127.0.0.1;false')
    $script:missingPort = 20493
    Expect-Failure 'missing port 20493'
    Assert ($script:events.Count -eq 0) 'missing server must return before gateway write'
    $script:missingPort = 0
    $script:gatewayExit = 1
    Expect-Failure 'could not select local login server'
    Assert (($script:events -join ',') -eq 'gateway-read,gateway-write') 'failed gateway reset must prevent launch'
    $script:gatewayExit = 0
    $NoClient = $true
    Expect-Failure '-ClientOnly cannot be combined'
    Assert ($script:events.Count -eq 0) 'invalid options must return before gateway write'
    $NoClient = $false

    # Server ownership: this checkout's servers and what they started, never another checkout's (even one whose folder
    # name starts with this one's), a build, or a test run.
    $t = [datetime]'2026-10-30T20:00:00'
    function P($id, $parent, $name, $line, $exe = $null) {
        [pscustomobject]@{ ProcessId = $id; ParentProcessId = $parent; Name = $name; CommandLine = $line; ExecutablePath = $exe; CreationDate = $t.AddSeconds($id) }
    }
    $script:processes = @(
        # this checkout, started by an older live.ps1: relative paths, the cmd wrapper names its log here
        (P 10 1 'cmd.exe' "cmd.exe /c dotnet src\GameServer.Auth\bin\Debug\net10.0\GameServer.Auth.dll serve 1>`"$repo\logs\live-auth.log`"")
        (P 11 10 'dotnet.exe' 'dotnet  src\GameServer.Auth\bin\Debug\net10.0\GameServer.Auth.dll serve')
        # this checkout, the watched Gateway with absolute paths, and its tree
        (P 20 1 'cmd.exe' "cmd.exe /c dotnet watch run --project $repo\src\GameServer.Gateway --non-interactive -- 1>`"$repo\logs\live-gateway.log`"")
        (P 21 20 'dotnet.exe' "dotnet  watch run --project $repo\src\GameServer.Gateway --non-interactive --")
        (P 22 21 'dotnet.exe' "dotnet.exe dotnet-watch.dll run --project $repo\src\GameServer.Gateway")
        (P 23 22 'dotnet.exe' 'dotnet.exe run --no-build -e DOTNET_WATCH=1')
        (P 24 23 'GameServer.Gateway.exe' "`"$repo\src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe`"" "$repo\src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe")
        # another checkout whose folder name starts with this one's
        (P 30 1 'cmd.exe' "cmd.exe /c dotnet src\GameServer.Auth\bin\Debug\net10.0\GameServer.Auth.dll serve 1>`"$repo-b\logs\live-auth.log`"")
        (P 31 30 'dotnet.exe' 'dotnet  src\GameServer.Auth\bin\Debug\net10.0\GameServer.Auth.dll serve')
        (P 32 1 'GameServer.Gateway.exe' "`"$repo-b\src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe`"" "$repo-b\src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.exe")
        # a build and a test run here
        (P 40 1 'dotnet.exe' "dotnet build $repo\src\GameServer.Auth -v q")
        (P 41 1 'dotnet.exe' "dotnet test $repo\tests\GameServer.Gateway.Tests")
        # an older process whose reused PID is a later root's "parent"
        (P 5 24 'dotnet.exe' 'dotnet something-else.dll')
    )
    $own = @(Get-OwnServers 'Auth', 'Gateway' | ForEach-Object { $_.ProcessId } | Sort-Object)
    Assert (($own -join ',') -eq '10,11,20,21,22,23,24') "own servers: $($own -join ',')"
    $own = @(Get-OwnServers 'Auth' | ForEach-Object { $_.ProcessId } | Sort-Object)
    Assert (($own -join ',') -eq '10,11') "own Auth only: $($own -join ',')"
}
finally {
    $env:Game_PORT_BASE = $null
    Remove-Item $repo -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output 'live.ps1 client, port and ownership checks passed (mocked; no client, server or GGPK touched).'
