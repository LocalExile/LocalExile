# Extracts the game data the servers read from Client/Content.ggpk into data/ (all git-ignored; see
# Agents/GettingStarted.md step 7). Runs every entry of the manifest below whose output is missing or whose stamp in
# data/extract-stamps.json differs from the manifest's version, and skips the rest. live.ps1 runs it on every start.
# Read-only on the GGPK (it works while the client runs); never touches the client or the patch servers.
#   -Force         run every entry
#   -Only <name>   run only these entries (comma-separated), whatever their stamps say
#   -List          print the manifest and each entry's state, run nothing
# A change to a generator's output bumps its entry's Version in the same PR (AGENTS.md); everyone's next run
# regenerates it. An entry that runs also reruns the entries that need it (Needs).
#   -ExportManifest <file>   write the manifest as JSON and run nothing (a release's launcher reads it: scripts/release)
param([switch]$Force, [string[]]$Only = @(), [switch]$List, [string]$ExportManifest)
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# Name, outputs (the first is checked for presence and freshness), runner (dotnet | python), arguments, version, needs,
# and what reads it. Keep in step with the Gateway's GameDataFiles.cs.
$manifest = @(
    @{ Name = 'tables'; Outputs = @('data\tables\WorldAreas.json', 'data\tables\UniqueMaps.json', 'data\tables\Words.json', 'data\tables\VillageUniqueDisenchantValues.json', 'data\tables\ItemVisualIdentity.json', 'data\tables\BaseItemTypes.json', 'data\tables\ItemClasses.json', 'data\tables\Stats.json', 'data\tables\Mods.json', 'data\tables\Tags.json', 'data\tables\InfluenceTags.json', 'data\tables\UniqueStashLayout.json', 'data\tables\UniqueStashTypes.json', 'data\extract-report.json', 'data\tables\Essences.json', 'data\tables\CraftingBenchOptions.json', 'data\tables\CraftingItemClassCategories.json', 'data\tables\ModType.json', 'data\tables\PassiveJewelSlots.json', 'data\tables\DelveUpgrades.json', 'data\tables\DelveFlares.json', 'data\tables\DelveDynamite.json', 'data\tables\DelveLevelScaling.json', 'data\tables\LabyrinthTrials.json', 'data\tables\GrantedEffectStatSets.json', 'data\tables\BuffDefinitions.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'Tools/PoEDataExtractor', '--', 'decode', 'Client/Content.ggpk'); Version = 2; Needs = @()
       UsedBy = 'every game table: stats, levels, areas, waypoints, items, mods, monsters, NPCs; all other entries' }
    @{ Name = 'area-transitions'; Outputs = @('data\area-transitions.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/atlas', '--', 'Client/Content.ggpk', 'data/area-transitions.json'); Version = 3; Needs = @('tables')
       UsedBy = 'area transitions of fixed layouts (08-area-transitions.md)' }
    @{ Name = 'item-types'; Outputs = @('data\item-types.json'); Runner = 'python'
       Args = @('scripts/re/ot_components.py', '--items', '--json', 'data/item-types.json'); Version = 2; Needs = @('tables')
       UsedBy = 'items: starting items, /item, drops, affix rolls (tags), quest items'' Quest flags' }
    @{ Name = 'delve-paths'; Outputs = @('data\delve-paths.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/atlas', '--', 'Client/Content.ggpk', 'delve-paths'); Version = 1; Needs = @('tables')
       UsedBy = 'authored Delve rail nodes, endpoints and room links' }
    @{ Name = 'room-points'; Outputs = @('data\room-points.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/atlas', '--', 'Client/Content.ggpk', 'room-points'); Version = 6; Needs = @('tables')
       UsedBy = 'unique bosses and named packs (14-monster-packs.md), quest room objects (12-quests.md)' }
    # After area-transitions and room-points: --all also takes the object types those files name.
    @{ Name = 'object-types'; Outputs = @('data\object-types.json'); Runner = 'python'
       Args = @('scripts/re/ot_components.py', '--all', '--json', 'data/object-types.json'); Version = 6; Needs = @('tables', 'area-transitions', 'room-points')
       UsedBy = 'monsters (emergence and inherited Life corpse flags), NPCs, area transitions, quest objects and chests, hideout decorations and stationary collision footprints' }
    @{ Name = 'hideouts'; Outputs = @('data\hideouts.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/atlas', '--', 'Client/Content.ggpk', 'hideouts'); Version = 3; Needs = @('tables', 'object-types')
       UsedBy = 'hideouts and their decorations (21-hideouts.md)' }
    @{ Name = 'animation-contacts'; Outputs = @('data\animation-contacts.json'); Runner = 'python'
       Args = @('scripts/re/anim_contacts.py'); Version = 3; Needs = @('tables')
       UsedBy = 'class spell contacts, ACT-selected ordinary wand release timing and the contact keyframes'' positions: where an arrow or wand bolt leaves (11-skills.md)' }
    # monster-contacts: the clips a variety plays for its projectile attacks, with their contact keyframe's time and position:
    # when and from where a Sand Spitter mortar leaves (the skill's own animation key, the native clip selector).
    @{ Name = 'monster-contacts'; Outputs = @('data\monster-contacts.json'); Runner = 'python'
       Args = @('scripts/re/monster_contacts.py'); Version = 2; Needs = @('tables')
       UsedBy = 'monster projectile release timing and points (Program.ProjectileHostile.cs, 11-skills.md section 6)' }
    # monster-takehit also gives each variety's Emerge clip and its length: how long an emerge monster takes to rise.
    @{ Name = 'monster-takehit'; Outputs = @('data\monster-takehit.json'); Runner = 'python'
       Args = @('scripts/re/monster_takehit.py'); Version = 3; Needs = @('tables')
       UsedBy = 'monster hit reaction: each variety''s TakeHit clip indexes in its rig and their lengths (11-skills.md, 0x1b2)' }
    # projectile-clips: a server-created projectile object plays nothing by itself; the Gateway starts its rig's flight
    # clip at spawn and its impact clip where it stops (the Sand Spitter mortar is invisible otherwise).
    @{ Name = 'projectile-clips'; Outputs = @('data\projectile-clips.json'); Runner = 'python'
       Args = @('scripts/re/projectile_clips.py'); Version = 1; Needs = @('tables')
       UsedBy = 'server-created projectile objects (monster mortars): each Projectiles row''s flight and impact clip indexes and lengths in its rig, and its .ao default animation (11-skills.md)' }
    @{ Name = 'passive-graphs'; Outputs = @('data\passive-skill-graphs.json', 'data\phrecia-passive-skillgraph.psg'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/passive-graphs', '--', 'Client/Content.ggpk', 'data/passive-skill-graphs.json'); Version = 5; Needs = @('tables')
       UsedBy = 'ordinary/ascendancy server membership and connectivity; derived Phrecia client artifact remains uninstalled' }
    # The servers never open Client/ as they run: what they would read from it comes from these two.
    @{ Name = 'client-constants'; Outputs = @('data\client-constants.json'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/game-pack', '--', 'constants'); Version = 1; Needs = @()
       UsedBy = 'the Gateway''s HNC mask and the Patch server''s root hash (live.ps1 refreshes the hash before each client start)' }
    @{ Name = 'game-pack'; Outputs = @('data\game-files.zip'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/game-pack', '--', 'pack'); Version = 1; Needs = @()
       UsedBy = 'the files of server-side level generation (ServerLevels) and the Phrecia classes'' graph check (GamePack)' }
    @{ Name = 'stat-descriptions'; Outputs = @('data\stat-descriptions.txt'); Runner = 'dotnet'
       Args = @('run', '--project', 'scripts/re/game-pack', '--', 'stat-descriptions'); Version = 1; Needs = @()
       UsedBy = 'the mod wording of the Gateway''s crafter mode, for tools such as the launcher''s item crafter (item-crafter.md)' }
)

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }

if ($ExportManifest) {
    ConvertTo-Json -Depth 4 -InputObject @($manifest | ForEach-Object { [ordered]@{ Name = $_.Name; Outputs = @($_.Outputs); Runner = $_.Runner; Args = @($_.Args); Version = $_.Version; Needs = @($_.Needs) } }) |
        Set-Content $ExportManifest -Encoding ascii
    return
}

$unknown = $Only | Where-Object { $n = $_; -not ($manifest | Where-Object { $_.Name -eq $n }) }
if ($unknown) { throw "extract-data: unknown entry $($unknown -join ', '); entries: $(($manifest | ForEach-Object { $_.Name }) -join ', ')" }

$stampFile = Join-Path $repo 'data\extract-stamps.json'
$stamps = @{}
if (Test-Path $stampFile) {
    try { (Get-Content $stampFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $stamps[$_.Name] = $_.Value } }
    catch { Write-Output "extract-data: $stampFile unreadable, treating every entry as unstamped" }
}

function Why($e) {
    $missing = $e.Outputs | Where-Object { -not (Test-Path (Join-Path $repo $_)) } | Select-Object -First 1
    if ($missing) { return "$missing missing" }
    $s = $stamps[$e.Name]
    if (-not $s) { return 'no stamp' }
    if ([int]$s.version -ne $e.Version) { return "version $($s.version) -> $($e.Version)" }
    return $null
}

if ($List) {
    foreach ($e in $manifest) {
        $why = Why $e
        Write-Output ("{0,-20} v{1}  {2,-28} {3}" -f $e.Name, $e.Version, $(if ($why) { "stale: $why" } else { 'up to date' }), $e.UsedBy)
    }
    return
}

# Decide what runs, in manifest order (an entry comes after the ones it needs).
$todo = [System.Collections.Generic.List[object]]::new()
$reason = @{}
foreach ($e in $manifest) {
    $why = if ($Only) { if ($Only -contains $e.Name) { 'asked (-Only)' } } elseif ($Force) { 'forced' } else { Why $e }
    if (-not $why -and -not $Only) {
        $dep = $e.Needs | Where-Object { $reason.ContainsKey($_) } | Select-Object -First 1
        if ($dep) { $why = "$dep reruns" }
    }
    if ($why) { $todo.Add($e); $reason[$e.Name] = $why }
}
if ($todo.Count -eq 0) { Write-Output 'game data up to date'; return }

Need (Test-Path (Join-Path $repo 'Client\Content.ggpk')) 'Client\Content.ggpk missing: this needs the standalone client layout'
Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Get-Command node -ErrorAction SilentlyContinue) 'Node.js not found (node): it decompresses the game bundles'
Need (Test-Path (Join-Path $repo 'Tools\ooz\node_modules\ooz-wasm')) 'Tools\ooz\node_modules missing: cd Tools\ooz; npm install'
$python = $null
if ($todo | Where-Object { $_.Runner -eq 'python' }) {
    # A virtual environment in the repository (.venv) wins over the python on PATH, as in live.ps1.
    $venv = Join-Path $repo '.venv\Scripts\python.exe'
    $python = if (Test-Path $venv) { $venv } else { (Get-Command python -ErrorAction SilentlyContinue).Source }
    if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
    Need $python 'Python 3.9+ not found (python)'
    & $python -c 'import sys; sys.exit(sys.version_info < (3, 9))' 2>$null
    Need ($LASTEXITCODE -eq 0) "Python 3.9+ needed ($python)"
}

$logs = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null

# Runs $exe with its output in $log and $log.err and returns its exit code. Output goes to files: a pipe would keep
# PowerShell 5.1 from seeing the exit code, and the logs stay readable.
# It waits for the process itself, not with Start-Process -Wait: in Windows PowerShell 5.1 -Wait also waits for every
# process the child started, and dotnet build/run leaves an MSBuild worker node (MSBuild.dll /nodeReuse:true) running
# for minutes, so -Wait never returned (live.ps1 sat at "extracting tables ..." with the entry long done). The env vars
# set below keep those nodes from being left at all.
function Invoke-Logged($exe, $exeArgs, $log) {
    $p = Start-Process $exe -ArgumentList ($exeArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) `
        -WorkingDirectory $repo -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle  # keeps the handle open, or ExitCode reads as empty after the wait in PowerShell 5.1
    $p.WaitForExit()
    return $p.ExitCode
}
function Tail($log) { (Get-Content $log, "$log.err" -Tail 15 -ErrorAction SilentlyContinue) -join "`n" }

$savedEnv = @{}
foreach ($kv in @{ MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_USE_MSBUILD_SERVER = '0' }.GetEnumerator()) {
    $savedEnv[$kv.Key] = [Environment]::GetEnvironmentVariable($kv.Key)
    [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
}
try {
    # Build each dotnet project once, then run it with --no-build: the atlas entries share one project.
    $projects = @($todo | Where-Object { $_.Runner -eq 'dotnet' } | ForEach-Object { $_.Args[2] } | Select-Object -Unique)
    foreach ($proj in $projects) {
        $log = Join-Path $logs "extract-build-$(Split-Path $proj -Leaf).log"
        Write-Output "building $proj ..."
        $code = Invoke-Logged 'dotnet' @('build', $proj) $log
        if ($code -ne 0) { throw "extract-data: building $proj failed (exit $code; logs\$(Split-Path $log -Leaf)):`n$(Tail $log)" }
    }
    $done = 0
    foreach ($e in $todo) {
        $done++
        $log = Join-Path $logs "extract-$($e.Name).log"
        Write-Output "extracting [$done/$($todo.Count)] $($e.Name) ($($reason[$e.Name])) ..."
        $started = Get-Date
        $exe = 'dotnet'; $exeArgs = @('run', '--no-build') + $e.Args[1..($e.Args.Count - 1)]
        if ($e.Runner -eq 'python') { $exe = $python; $exeArgs = @('-I') + $e.Args }
        $code = Invoke-Logged $exe $exeArgs $log
        $output = Join-Path $repo $e.Outputs[0]
        $fresh = (Test-Path $output) -and (Get-Item $output).LastWriteTime -ge $started.AddSeconds(-2)
        if ($code -ne 0 -or -not $fresh) {
            throw "extract-data: $($e.Name) failed (exit $code$(if (-not $fresh) { ", $($e.Outputs[0]) not written" }); logs\extract-$($e.Name).log):`n$(Tail $log)"
        }
        $stamps[$e.Name] = [pscustomobject]@{ version = $e.Version; at = (Get-Date).ToString('s') }
        $stamps | ConvertTo-Json | Set-Content $stampFile -Encoding utf8
        Write-Output "  done in $([int]((Get-Date) - $started).TotalSeconds) s"
    }
}
finally { foreach ($k in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $savedEnv[$k]) } }
Write-Output "game data extracted: $(($todo | ForEach-Object { $_.Name }) -join ', ')"
