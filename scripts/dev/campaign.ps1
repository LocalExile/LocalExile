# One command for the campaign runner (Agents/docs/dev/campaign-automation.md):
#   starts this checkout's servers when they are not running (live.ps1 -NoClient), then runs scripts/dev/campaign/runner.py,
#   which opens its own session client through the control bridge (live.ps1 -ClientOnly -Session), logs in (password from
#   data/offline-login.txt, else one prompt), creates a fresh character (or continues -Character), turns god mode on and
#   plays the quests in order. Quests without a step file (acts not on the quest engine yet) are reported "not implemented".
#   -Character <name>   continue this character instead of creating one
#   -Class <Witch>      class of the new character; -Name sets its name (default Camp + time, letters only)
#   -Only/-From/-To     quest Ids from scripts/dev/campaign/order.py (-Only takes a comma-separated list)
#   -Preset <Id>        /quest_preset first (a CharacterStartQuestState Id such as Act1Complete)
#   -NoGod              leave god mode off
#   -RandomSeeds        let new instances use random seeds (default: Game_RANDOM_SEEDS=0, seed 1, whose captures the
#                       quest objects are placed from); only takes effect when this script starts the servers
#   -NoBuild            pass -NoBuild to live.ps1
#   -PortBase N         this checkout's servers on ports N..N+4 (live.ps1 -PortBase, remembered in data\stack.json); without
#                       it Game_PORT_BASE, else the base data\stack.json records, else the default ports
#   -Session S          the client session the run drives (default 'campaign'; live.ps1 -Session): its own working folder,
#                       settings and PID record, so the bridge never attaches to another session's or checkout's client
#   -Nullifier          that session's client without a GPU (live.ps1 -Nullifier: the Null renderer, about 1.6 GB commit)
#   -AllowDefaultSaves  run without Game_SAVES_DB, on data\saves.db (the run creates characters and fills the stash there)
#   Any other arguments go to runner.py unchanged (e.g. --replace, --dry-run, --stop-on-fail, --retries 1).
# Set $env:Game_SAVES_DB to a copied save database before running; this script never changes it, refuses to run without
# it (unless -AllowDefaultSaves), and refuses running servers whose Gateway logged another save database. Servers on this
# stack's ports that are not this checkout's (another checkout or program) stop the run: give this checkout its own
# -PortBase. The variables it sets for the servers and the client (Game_RANDOM_SEEDS, Game_CLIENT_SESSION,
# Game_CLIENT_NULLIFIER) get their previous values back when it ends.
# No positional binding: a runner flag such as --list must reach $Rest, not become -Character.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Character,
    [ValidateSet('Marauder', 'Witch', 'Scion', 'Ranger', 'Duelist', 'Shadow', 'Templar')][string]$Class = 'Witch',
    [string]$Name,
    [string[]]$Only,
    [string]$From,
    [string]$To,
    [string]$Preset,
    [switch]$NoGod,
    [switch]$RandomSeeds,
    [switch]$NoBuild,
    [int]$PortBase = -1,
    [string]$Session = 'campaign',
    [switch]$Nullifier,
    [switch]$AllowDefaultSaves,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$started = Get-Date

if (-not $Session -or $Session -eq 'default') {
    if ($Nullifier) { throw '-Nullifier needs a named -Session: the default client starts as before' }
    $Session = 'default'
}
elseif ($Session -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,23}$') { throw "session name '$Session': up to 24 letters, digits, - and _" }

# The save database the run must play on (the servers resolve a relative path from the repository root, their working
# folder). Without Game_SAVES_DB that is data\saves.db, the user's own saves: only with -AllowDefaultSaves.
if ($env:Game_SAVES_DB) { $saves = [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $env:Game_SAVES_DB)) }
elseif ($AllowDefaultSaves) { $saves = Join-Path $repo 'data\saves.db' }
else {
    throw ('Game_SAVES_DB is not set: the run would create its characters, stash its items and (--replace) delete ' +
        'characters in data\saves.db. Set it to a copied database ($env:Game_SAVES_DB = "$PWD\data\campaign-saves.db"), ' +
        'or give -AllowDefaultSaves to play on data\saves.db')
}
Write-Output "save database: $saves$(if (-not $env:Game_SAVES_DB) { ' (Game_SAVES_DB is not set: -AllowDefaultSaves)' })"

# The save database this checkout's Gateway opened: the last "[gateway] saves: <path>" line of its log (Program.cs), as
# a full path; null without one.
function Get-GatewaySaves {
    $log = Join-Path $repo 'logs\live-gateway.log'
    if (-not (Test-Path -LiteralPath $log)) { return $null }
    $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite, Delete')
    try { $text = (New-Object IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
    $found = [regex]::Matches($text, '\[gateway\] saves: (.+?)\s*$', 'Multiline')
    if (-not $found.Count) { return $null }
    [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $found[$found.Count - 1].Groups[1].Value))
}

# The running Gateway's save database must be the run's: refused otherwise, with the path it really uses.
function Assert-GatewaySaves([string]$when) {
    $actual = Get-GatewaySaves
    if (-not $actual) { throw "$when, but logs\live-gateway.log has no '[gateway] saves:' line: which save database it uses is unknown" }
    Write-Output "the Gateway's save database: $actual"
    if (-not [string]::Equals($actual, $saves, [StringComparison]::OrdinalIgnoreCase)) {
        throw ("this checkout's Gateway plays on $actual, not on $saves. Stop its servers (.\scripts\dev\live.ps1 -Stop) " +
            'and run again: this script starts them with Game_SAVES_DB')
    }
}

# This checkout's Patch, Auth login and Gateway ports, as live.ps1 picks them: -PortBase, else Game_PORT_BASE, else the base
# data\stack.json records, else the servers' own variables or the defaults.
function Get-StackPorts {
    $base = $PortBase
    if ($base -lt 0 -and "$env:Game_PORT_BASE" -ne '') { $base = [int]$env:Game_PORT_BASE }
    $file = Join-Path $repo 'data\stack.json'
    if ($base -lt 0 -and (Test-Path $file)) { $base = [int](Get-Content $file -Raw | ConvertFrom-Json).portBase }
    if ($base -gt 0) { return @($base, ($base + 2), ($base + 4)) }
    function Pick([string]$name, [int]$default) { $v = [Environment]::GetEnvironmentVariable($name); if ($v) { [int]$v } else { $default } }
    @((Pick 'Game_PATCH_TCP_PORT' 12995), (Pick 'Game_LOGIN_PORT' 20493), (Pick 'Game_GATEWAY_PORT' 6112))
}

# Whether a process names a path inside this checkout on its command line or as its executable (live.ps1's Test-Here: a
# git worktree nested in it, .claude\worktrees\<name>, is another checkout).
function Test-Here($process) {
    $null -ne $process -and [regex]::IsMatch("$($process.CommandLine)|$($process.ExecutablePath)",
        [regex]::Escape("$repo\") + '(?!\.claude\\worktrees\\)', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

# Per port: 'free', 'ours' (this checkout's server, or its parent is) or the foreign listener's description.
function Get-PortOwners([int[]]$ports) {
    foreach ($port in $ports) {
        $listeners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
        if (-not $listeners.Count) { [pscustomobject]@{ Port = $port; Owner = 'free' }; continue }
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($listeners[0].OwningProcess)" -ErrorAction SilentlyContinue
        $parent = if ($proc) { Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.ParentProcessId)" -ErrorAction SilentlyContinue }
        $ours = $proc -and $proc.Name -match '^(dotnet|GameServer\.\w+)\.exe$' -and ((Test-Here $proc) -or (Test-Here $parent))
        $owner = if ($ours) { 'ours' } elseif ($proc) { "$($proc.Name) (pid $($proc.ProcessId)) $($proc.ExecutablePath)" } else { "pid $($listeners[0].OwningProcess)" }
        [pscustomobject]@{ Port = $port; Owner = $owner }
    }
}

# Set for the servers and the client this run starts; the caller's values come back in the finally below.
$previous = @{}
foreach ($envName in 'Game_RANDOM_SEEDS', 'Game_CLIENT_SESSION', 'Game_CLIENT_NULLIFIER') {
    $previous[$envName] = [Environment]::GetEnvironmentVariable($envName, 'Process')
}
$code = 1
$pushed = $false
try {
if (-not $RandomSeeds) { $env:Game_RANDOM_SEEDS = '0' }

$owners = @(Get-PortOwners (Get-StackPorts))
$foreign = @($owners | Where-Object { $_.Owner -notin 'free', 'ours' })
if ($foreign.Count) {
    throw ("port(s) $(($foreign | ForEach-Object { "$($_.Port): $($_.Owner)" }) -join '; ') are not this checkout's servers: " +
        'the run would play on them. Give this checkout its own ports with -PortBase (Agents/docs/dev/live-testing.md)')
}
if (@($owners | Where-Object { $_.Owner -eq 'free' }).Count) {
    Write-Output "starting this checkout's servers (ports not listening: $(($owners | Where-Object { $_.Owner -eq 'free' } | ForEach-Object { $_.Port }) -join ', '))"
    $live = @{ NoClient = $true }
    if ($NoBuild) { $live.NoBuild = $true }
    if ($PortBase -ge 0) { $live.PortBase = $PortBase }
    & (Join-Path $PSScriptRoot 'live.ps1') @live
    Assert-GatewaySaves 'the servers started'
}
else {
    Write-Output "this checkout's servers already run on ports $($owners.Port -join ', ') (their Game_RANDOM_SEEDS is the one they started with)"
    Assert-GatewaySaves 'the servers run'
}

# The run's client: game:open starts it with live.ps1 -ClientOnly -Session (these variables), and the bridge finds it by
# the PID record of that session (data\clients\<session>.json) and this checkout's Client path.
$env:Game_CLIENT_SESSION = $Session
if ($Nullifier) { $env:Game_CLIENT_NULLIFIER = '1' }
Write-Output "client session: $Session$(if ($Nullifier) { ' (Nullifier)' })"

$python = Join-Path $repo '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { $python = (Get-Command python -ErrorAction Stop).Source }
$arguments = @((Join-Path $PSScriptRoot 'campaign\runner.py'), '--session', $Session)
if ($Character) { $arguments += '--character', $Character }
else {
    $arguments += '--new', '--class', $Class
    if ($Name) { $arguments += '--name', $Name }
}
# -Only a1q4,a1q5 arrives as an array (PowerShell splits the commas); the runner takes one comma-separated value.
if ($Only) { $arguments += '--only', (($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) -join ',') }
if ($From) { $arguments += '--from', $From }
if ($To) { $arguments += '--to', $To }
if ($Preset) { $arguments += '--preset', $Preset }
if ($NoGod) { $arguments += '--no-god' }
$arguments += $Rest

# The runner writes logs\campaign-<time>.summary.json after a run; its short form ends this script's output.
function Write-CampaignSummary {
    $file = Get-ChildItem -Path (Join-Path $repo 'logs') -Filter 'campaign-*.summary.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return }
    $r = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $count = { param($status) if ($r.quests.PSObject.Properties[$status]) { $r.quests.$status } else { 0 } }
    $wall = (Get-Date) - $started
    Write-Output ''
    Write-Output '==== campaign summary ===='
    Write-Output ("character    {0}, level {1}, in {2} ({3})" -f $r.character, $r.level, $r.areaName, $r.area)
    Write-Output ("quests       {0} done, {1} already done, {2} failed, {3} not implemented, {4} blocked" -f `
        (& $count 'done'), (& $count 'skipped (already)'), (& $count 'failed'), (& $count 'not implemented'), (& $count 'blocked'))
    foreach ($f in $r.failed) { Write-Output ("  failed     {0}: {1}" -f $f.quest, $f.note) }
    Write-Output ("passives     {0} unspent ({1} from quests), {2} allocated; refund points {3}" -f `
        $r.passives.unspent, $r.passives.quest, $r.passives.allocated, $r.passives.refund)
    Write-Output ("pantheon     major {0}, minor {1}; Kitava resistance penalty {2}%" -f `
        $(if ($r.pantheon.major) { $r.pantheon.major } else { '-' }), $(if ($r.pantheon.minor) { $r.pantheon.minor } else { '-' }), $r.resistancePenalty)
    foreach ($p in $r.presets.PSObject.Properties) {
        $state = if (@($p.Value).Count) { "missing $(@($p.Value) -join ', ')" } else { 'all flags set' }
        Write-Output ("preset       {0}: {1}" -f $p.Name, $state)
    }
    if (@($r.missingDone).Count) { Write-Output ("DONE missing {0}" -f (@($r.missingDone) -join ', ')) }
    Write-Output ("time         runner {0:n0} s ({1:hh\:mm\:ss}); whole command {2:hh\:mm\:ss}" -f `
        $r.seconds, [TimeSpan]::FromSeconds($r.seconds), $wall)
    Write-Output ("details      {0}" -f ($file.FullName -replace '\.summary\.json$', '.log'))
}

Push-Location $repo
$pushed = $true
& $python @arguments
$code = $LASTEXITCODE
Write-CampaignSummary
}
finally {
    if ($pushed) { Pop-Location }
    foreach ($envName in $previous.Keys) { [Environment]::SetEnvironmentVariable($envName, $previous[$envName], 'Process') }
}
exit $code
