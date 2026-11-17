<#
  LocalExile Filter Fixer
  -----------------------
  Converts a loot filter made for a newer Path of Exile patch (e.g. a 3.30 FilterBlade / NeverSink filter)
  into one the LocalExile 3.29 client will load.

  It removes only what the older client doesn't know:
    - BaseType / Class / HasExplicitMod / HasImplicitMod values that don't exist in YOUR client's data
    - blocks that use a filter condition the 3.29 client doesn't understand (e.g. "Fated")
  Everything else (styles, sounds, tiers, your FilterBlade customisations) is kept as-is.

  No game data ships with this tool: item names are read from the LocalExile folder (the client tables in
  sql\updates\client\, or data\tables\*.json in older extracts).

  Usage:
    Double-click "Fix Loot Filter.bat"                         -> window
    Drag a .filter file onto "Fix Loot Filter.bat"             -> converts it (window opens with it selected)
    powershell -File LocalExileFilterFixer.ps1 -FilterPath x -LocalExilePath y [-OutPath z] [-NoGui]
#>
param(
    [string]$FilterPath,
    [string]$LocalExilePath,
    [string]$OutPath,
    [switch]$NoGui
)

$ErrorActionPreference = 'Stop'
$ToolVersion = '1.0'

# ---------------------------------------------------------------------------------------------
# Filter keywords the 3.29.3 client understands (checked against the 3.29.3 Game.exe).
# Anything not on this list is treated as "from a newer patch".
# ---------------------------------------------------------------------------------------------
$KnownBlockStarts = @('Show', 'Hide', 'Minimal')
$KnownConditions = @(
    'AreaLevel','ItemLevel','DropLevel','Quality','Rarity','Class','BaseType','LinkedSockets','SocketGroup','Sockets',
    'Height','Width','HasExplicitMod','HasImplicitMod','AnyEnchantment','HasEnchantment','EnchantmentPassiveNode',
    'EnchantmentPassiveNum','StackSize','GemLevel','AlternateQuality','Replica','Identified','Corrupted','CorruptedMods',
    'Mirrored','ElderItem','ShaperItem','HasInfluence','FracturedItem','SynthesisedItem','ShapedMap','ElderMap',
    'BlightedMap','UberBlightedMap','MapTier','HasSearingExarchImplicit','HasEaterOfWorldsImplicit','ArchnemesisMod',
    'Scourged','TransfiguredGem','HasCruciblePassiveTree','BaseDefencePercentile','BaseArmour','BaseEnergyShield',
    'BaseEvasion','BaseWard','ZanaMemory','MemoryStrands','Imbued','Foulborn','Vestigial','UnidentifiedItemTier'
)
$KnownActions = @(
    'SetBorderColor','SetTextColor','SetBackgroundColor','SetFontSize','PlayAlertSound','PlayAlertSoundPositional',
    'CustomAlertSound','CustomAlertSoundOptional','DisableDropSound','EnableDropSound','DisableDropSoundIfAlertSound',
    'EnableDropSoundIfAlertSound','MinimapIcon','PlayEffect','Continue'
)
$Known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($k in ($KnownConditions + $KnownActions + 'Import')) { [void]$Known.Add($k) }

$SettingsDir  = Join-Path $env:APPDATA 'LocalExileFilterFixer'
$SettingsFile = Join-Path $SettingsDir 'settings.json'

# ---------------------------------------------------------------------------------------------
# Locating things
# ---------------------------------------------------------------------------------------------
# The game (LocalExile's client too) lists loot filters in <Documents>\My Games\Path of Exile\ and its OnlineFilters\
# subfolder - never in other subfolders. LocalExile keeps its own settings in a "LocalExile" subfolder, but filters
# placed there are NOT seen by the game, so output always goes to the "Path of Exile" folder itself.
function Find-PoeFilterFolder {
    $docRoots = New-Object System.Collections.Generic.List[string]
    $docRoots.Add([Environment]::GetFolderPath('MyDocuments'))      # where the game looks (follows OneDrive redirection)
    if ($env:OneDrive) { $docRoots.Add((Join-Path $env:OneDrive 'Documents')) }
    $docRoots.Add((Join-Path $env:USERPROFILE 'OneDrive\Documents'))
    $docRoots.Add((Join-Path $env:USERPROFILE 'Documents'))
    $cands = @($docRoots | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'My Games\Path of Exile' } | Select-Object -Unique)

    # Prefer the folder the game has actually used: it writes *_Config.ini there (or in a subfolder like LocalExile\)
    foreach ($c in $cands) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        $ini = Get-ChildItem -LiteralPath $c -Filter '*_Config.ini' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $ini) {
            $ini = Get-ChildItem -LiteralPath $c -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*_Config.ini' -File -ErrorAction SilentlyContinue } |
                Select-Object -First 1
        }
        if ($ini) { return $c }
    }
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { return $c } }
    return $cands[0]
}

# Turns whatever folder someone picked into the folder the game reads filters from.
function Resolve-PoeFilterFolder([string]$folder) {
    if (-not $folder) { return $null }
    $f = $folder.TrimEnd('\')
    # Picked a subfolder of "Path of Exile" (LocalExile\, OnlineFilters\, ...) -> use "Path of Exile" itself
    $cur = $f
    while ($cur) {
        if ((Split-Path $cur -Leaf) -eq 'Path of Exile' -and (Split-Path (Split-Path $cur -Parent) -Leaf) -eq 'My Games') { return $cur }
        $cur = Split-Path $cur -Parent
    }
    # Picked Documents or My Games -> go down to "Path of Exile"
    if ((Split-Path $f -Leaf) -eq 'My Games') { return (Join-Path $f 'Path of Exile') }
    if (Test-Path -LiteralPath (Join-Path $f 'My Games')) { return (Join-Path $f 'My Games\Path of Exile') }
    return $f
}

function Get-PoeDocsFolder {
    if ($script:FilterFolder) { return $script:FilterFolder }
    return (Find-PoeFilterFolder)
}

# Where the client's item tables are, under a LocalExile folder (a release or a source checkout):
#   sql\updates\client\*_BaseItemTypes.sql ...  the client tables as SQL, shipped in every release and checkout (preferred)
#   data\tables\BaseItemTypes.json ...          older extracts
function Get-DataSourceAt([string]$dir) {
    $sql = Join-Path $dir 'sql\updates\client'
    if (Test-Path -LiteralPath $sql) {
        if (Get-ChildItem -LiteralPath $sql -Filter '*_BaseItemTypes.sql' -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
            return @{ Kind = 'sql'; Dir = $sql }
        }
    }
    foreach ($d in @((Join-Path $dir 'data\tables'), (Join-Path $dir 'tables'))) {
        if (Test-Path -LiteralPath (Join-Path $d 'BaseItemTypes.json')) { return @{ Kind = 'json'; Dir = $d } }
    }
    return $null
}

function Find-TablesFolder([string]$root) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { return $null }
    $src = Get-DataSourceAt $root
    if ($src) { return $src }
    # Look one level down (a release zip may be nested one folder deep)
    try {
        foreach ($sub in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $src = Get-DataSourceAt $sub.FullName
            if ($src) { return $src }
        }
    } catch { }
    return $null
}

function Get-Settings {
    try {
        if (Test-Path -LiteralPath $SettingsFile) { return (Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json) }
    } catch { }
    return $null
}

function Save-Settings([string]$localExile) {
    try {
        if (-not (Test-Path -LiteralPath $SettingsDir)) { New-Item -ItemType Directory -Path $SettingsDir | Out-Null }
        @{ LocalExilePath = $localExile; FilterFolder = $script:FilterFolder } | ConvertTo-Json | Set-Content -LiteralPath $SettingsFile -Encoding UTF8
    } catch { }
}

# Pick the output folder once at startup: saved choice if it still exists, otherwise detect it.
$script:FilterFolder = $null
$savedSettings = Get-Settings
if ($savedSettings -and $savedSettings.FilterFolder -and (Test-Path -LiteralPath $savedSettings.FilterFolder)) {
    $script:FilterFolder = Resolve-PoeFilterFolder $savedSettings.FilterFolder
} else {
    $script:FilterFolder = Find-PoeFilterFolder
}

function Find-LocalExile {
    # The LocalExile folder this tool sits in (scripts\loot-filter\ in a checkout, LootFilterFixer\ in a release),
    # then the one picked last time.
    $cands = New-Object System.Collections.Generic.List[string]
    $here = $PSScriptRoot
    while ($here) { $cands.Add($here); $here = Split-Path $here -Parent }
    $s = Get-Settings
    if ($s -and $s.LocalExilePath) { $cands.Add($s.LocalExilePath) }
    foreach ($c in $cands) {
        $t = Find-TablesFolder $c
        if ($t) { return $c }
    }
    return $null
}

# ---------------------------------------------------------------------------------------------
# Reading the client's own item data
# ---------------------------------------------------------------------------------------------
function Read-NameSet([string]$jsonPath) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $text = [IO.File]::ReadAllText($jsonPath, [Text.Encoding]::UTF8)
    $rx = [regex]'"Name":"((?:[^"\\]|\\.)*)"'
    foreach ($m in $rx.Matches($text)) {
        $v = $m.Groups[1].Value
        if ($v.Length -gt 0) {
            if ($v.Contains('\')) { $v = [regex]::Unescape($v) }
            [void]$set.Add($v)
        }
    }
    return ,$set
}

# Reads the "Name" column of one client table from its sql\updates\client\*_<Table>.sql files
# (INSERT INTO "<Table>" ("_row", "Id", ..., "Name", ...) VALUES  then one "(...)," row per line).
function Read-SqlNameSet([string]$sqlDir, [string]$table) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $files = @(Get-ChildItem -LiteralPath $sqlDir -Filter "*_$table.sql" -File -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($files.Count -eq 0) { return $null }
    $headRx = [regex]('^INSERT INTO "' + [regex]::Escape($table) + '" \(([^)]*)\) VALUES')
    $valRx  = [regex]"\s*('(?:[^']|'')*'|[^,]*)\s*(,|$)"
    foreach ($file in $files) {
        $idx = -1
        foreach ($line in [IO.File]::ReadLines($file.FullName, [Text.Encoding]::UTF8)) {
            if ($line.StartsWith('INSERT INTO')) {
                $m = $headRx.Match($line)
                if ($m.Success) {
                    $cols = @($m.Groups[1].Value.Split(',') | ForEach-Object { $_.Trim().Trim('"') })
                    $idx = [array]::IndexOf($cols, 'Name')
                }
                continue
            }
            if ($idx -lt 0 -or -not $line.StartsWith('(')) { continue }
            $pos = 1; $v = $null
            for ($k = 0; $k -le $idx; $k++) {
                $m = $valRx.Match($line, $pos)
                if (-not $m.Success) { $v = $null; break }
                $v = $m.Groups[1].Value
                $pos = $m.Index + $m.Length
            }
            if ($v -and $v.StartsWith("'") -and $v.Length -gt 2) {
                [void]$set.Add($v.Substring(1, $v.Length - 2).Replace("''", "'"))
            }
        }
    }
    return ,$set
}

function Load-ClientData($source, [scriptblock]$log) {
    & $log "Reading item data from $($source.Dir) ..."
    $data = @{}
    if ($source.Kind -eq 'sql') {
        $data.BaseType = Read-SqlNameSet $source.Dir 'BaseItemTypes'
        $data.Class    = Read-SqlNameSet $source.Dir 'ItemClasses'
        $data.Mod      = Read-SqlNameSet $source.Dir 'Mods'
    } else {
        $data.BaseType = Read-NameSet (Join-Path $source.Dir 'BaseItemTypes.json')
        $data.Class    = Read-NameSet (Join-Path $source.Dir 'ItemClasses.json')
        $modsPath = Join-Path $source.Dir 'Mods.json'
        $data.Mod = if (Test-Path -LiteralPath $modsPath) { Read-NameSet $modsPath } else { $null }
    }
    if (-not $data.BaseType -or $data.BaseType.Count -lt 1000) { throw "Couldn't read the item list from $($source.Dir)." }
    # One big newline-joined blob per set for fast substring ("contains") matching
    $data.Blob = @{}
    foreach ($k in @('BaseType','Class','Mod')) {
        if ($data[$k]) { $data.Blob[$k] = "`n" + [string]::Join("`n", [string[]]@($data[$k])) + "`n" }
    }
    & $log ("  {0} base types, {1} item classes, {2} mod names" -f $data.BaseType.Count, $data.Class.Count, $(if ($data.Mod) { $data.Mod.Count } else { 'no' }))
    return $data
}

function Test-ValueExists($data, [string]$kind, [string]$value, [bool]$exact) {
    $set = $data[$kind]
    if (-not $set) { return $true }   # no data for this kind -> don't touch it
    if ($exact) { return $set.Contains($value) }
    return ($data.Blob[$kind].IndexOf($value, [StringComparison]::Ordinal) -ge 0)
}

# ---------------------------------------------------------------------------------------------
# Conversion
# ---------------------------------------------------------------------------------------------
function Split-Comment([string]$line) {
    # returns @(codePart, commentPart) ; '#' never appears inside item names
    $i = $line.IndexOf('#')
    if ($i -lt 0) { return @($line, '') }
    return @($line.Substring(0, $i), $line.Substring($i))
}

function Convert-Filter([string]$inPath, $data, [scriptblock]$log) {
    $lines = [IO.File]::ReadAllLines($inPath, [Text.Encoding]::UTF8)
    $out = New-Object System.Collections.Generic.List[string]
    $stats = @{ Blocks = 0; DroppedBlocks = 0; RemovedValues = 0; RemovedLines = 0 }
    $removedValues = @{}
    $droppedBy = @{}
    $filterName = $null

    $i = 0
    # Strip the "#Online Item Filter / #name: / #version: ..." header that online-synced filters carry
    if ($lines.Count -gt 0 -and $lines[0].Trim() -eq '#Online Item Filter') {
        $i = 1
        while ($i -lt $lines.Count -and $lines[$i] -match '^#([A-Za-z]+):(.*)$') {
            if ($Matches[1] -eq 'name') { $filterName = $Matches[2].Trim() }
            $i++
        }
    }

    $out.Add("# Converted for LocalExile (3.29 client) by LocalExile Filter Fixer v$ToolVersion on $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $out.Add("# Source: $([IO.Path]::GetFileName($inPath))")

    $tokRx = [regex]'"[^"]*"|\S+'
    $opRx  = [regex]'^(==|=|!=|!|<=|>=|<|>)?\d*$'

    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $code = (Split-Comment $line)[0].Trim()
        $first = if ($code) { ($code -split '\s+', 2)[0] } else { '' }

        if ($KnownBlockStarts -notcontains $first) {
            $out.Add($line); $i++; continue
        }

        # ---- collect one block: header + following lines until blank line / next block
        $stats.Blocks++
        $block = New-Object System.Collections.Generic.List[string]
        $block.Add($line); $i++
        while ($i -lt $lines.Count) {
            $l = $lines[$i]
            if ($l.Trim() -eq '') { break }
            $c = (Split-Comment $l)[0].Trim()
            if ($c) {
                $f = ($c -split '\s+', 2)[0]
                if ($KnownBlockStarts -contains $f) { break }
            }
            $block.Add($l); $i++
        }

        # ---- process the block
        $newBlock = New-Object System.Collections.Generic.List[string]
        $newBlock.Add($block[0])
        $dropReason = $null
        for ($b = 1; $b -lt $block.Count -and -not $dropReason; $b++) {
            $l = $block[$b]
            $parts = Split-Comment $l
            $codePart = $parts[0]; $comment = $parts[1]
            $c = $codePart.Trim()
            if (-not $c) { $newBlock.Add($l); continue }
            $kw = ($c -split '\s+', 2)[0]
            $indent = $codePart.Substring(0, $codePart.Length - $codePart.TrimStart().Length)

            if (-not $Known.Contains($kw)) {
                if ($kw -match '^(Set|Play|Custom|Enable|Disable)' -or $kw -eq 'MinimapIcon') {
                    $stats.RemovedLines++
                    continue      # unknown cosmetic action: drop just the line
                }
                $dropReason = "condition '$kw' doesn't exist in 3.29"
                break
            }

            $kind = switch ($kw) {
                'BaseType'       { 'BaseType' }
                'Class'          { 'Class' }
                'HasExplicitMod' { 'Mod' }
                'HasImplicitMod' { 'Mod' }
                default          { $null }
            }
            if (-not $kind) { $newBlock.Add($l); continue }

            $tokens = @($tokRx.Matches($c.Substring($kw.Length)) | ForEach-Object { $_.Value })
            $prefix = New-Object System.Collections.Generic.List[string]
            $values = New-Object System.Collections.Generic.List[string]
            foreach ($t in $tokens) {
                if ($values.Count -eq 0 -and -not $t.StartsWith('"') -and $opRx.IsMatch($t)) { $prefix.Add($t) }
                else { $values.Add($t) }
            }
            $op = ($prefix -join '')
            $exact = $op.StartsWith('==')
            $negated = $op.StartsWith('!')

            $kept = New-Object System.Collections.Generic.List[string]
            foreach ($v in $values) {
                $raw = $v.Trim('"')
                if (Test-ValueExists $data $kind $raw $exact) { $kept.Add($v) }
                else {
                    $stats.RemovedValues++
                    $removedValues["$kw`: $raw"] = 1
                }
            }
            if ($kept.Count -eq $values.Count) { $newBlock.Add($l); continue }
            if ($kept.Count -eq 0) {
                if ($negated) { $stats.RemovedLines++; continue }   # "not any of <nothing>" -> condition is always true
                $dropReason = "all $kw values are from a newer patch"
                break
            }
            $rebuilt = $indent + $kw + ' ' + (($prefix + $kept) -join ' ')
            if ($comment) { $rebuilt += ' ' + $comment }
            $newBlock.Add($rebuilt)
        }

        if ($dropReason) {
            $stats.DroppedBlocks++
            $droppedBy[$dropReason] = 1 + [int]$droppedBy[$dropReason]
            $out.Add("# [LocalExile] block disabled: $dropReason")
            foreach ($l in $block) { $out.Add('#' + $l) }
        } else {
            foreach ($l in $newBlock) { $out.Add($l) }
        }
    }

    return @{ Lines = $out; Stats = $stats; Removed = $removedValues.Keys; DroppedBy = $droppedBy; Name = $filterName }
}

function Get-OutputPath([string]$inPath, [string]$filterName) {
    $base = if ($filterName) { $filterName } else { [IO.Path]::GetFileNameWithoutExtension($inPath) }
    foreach ($ch in [IO.Path]::GetInvalidFileNameChars()) { $base = $base.Replace([string]$ch, '') }
    $base = $base -replace '\s*-\s*LocalExile$', ''
    return (Join-Path (Get-PoeDocsFolder) ($base + ' - LocalExile.filter'))
}

function Invoke-Fix([string]$filter, [string]$localExile, [string]$outPath, [scriptblock]$log) {
    $tables = Find-TablesFolder $localExile
    if (-not $tables) { throw "Couldn't find LocalExile's item tables (sql\updates\client) under '$localExile'. Pick your LocalExile folder: the one with LocalExile.exe and the sql folder." }
    $data = Load-ClientData $tables $log
    & $log "Converting $filter ..."
    $r = Convert-Filter $filter $data $log
    if (-not $outPath) { $outPath = Get-OutputPath $filter $r.Name }
    $dir = Split-Path $outPath -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    [IO.File]::WriteAllLines($outPath, $r.Lines, (New-Object Text.UTF8Encoding($false)))
    if (-not (Test-Path -LiteralPath $outPath)) { throw "The filter couldn't be written to $outPath" }
    Save-Settings $localExile

    $s = $r.Stats
    & $log ""
    & $log ("Done. {0} blocks checked." -f $s.Blocks)
    & $log ("  {0} item names removed (not in your client)" -f $s.RemovedValues)
    & $log ("  {0} blocks disabled" -f $s.DroppedBlocks)
    foreach ($k in $r.DroppedBy.Keys) { & $log ("      {0} x {1}" -f $r.DroppedBy[$k], $k) }
    if ($s.RemovedLines) { & $log ("  {0} lines removed" -f $s.RemovedLines) }
    $sample = @($r.Removed | Sort-Object | Select-Object -First 12)
    if ($sample.Count) {
        & $log "  e.g. removed: $($sample -join ', ')$(if ($r.Removed.Count -gt 12) { ', ...' })"
    }
    & $log ""
    & $log "Saved: $outPath"
    $gameDir = (Get-PoeDocsFolder).TrimEnd('\')
    if ((Split-Path $outPath -Parent).TrimEnd('\') -ieq $gameDir) {
        & $log "That's the folder the game reads loot filters from."
    } else {
        & $log "WARNING: the game reads loot filters from $gameDir - it won't see a filter saved anywhere else."
    }
    & $log "In game: Escape > Options > Game > Item Filter > pick '$([IO.Path]::GetFileNameWithoutExtension($outPath))'"
    return $outPath
}

# ---------------------------------------------------------------------------------------------
# Command-line mode
# ---------------------------------------------------------------------------------------------
$consoleLog = { param($m) Write-Host $m }

if ($NoGui) {
    if (-not $FilterPath) { throw "-FilterPath is required with -NoGui" }
    if (-not $LocalExilePath) { $LocalExilePath = Find-LocalExile }
    if (-not $LocalExilePath) { throw "Couldn't find LocalExile. Pass -LocalExilePath." }
    if ($OutPath -and (Test-Path -LiteralPath $OutPath -PathType Container)) {
        $script:FilterFolder = Resolve-PoeFilterFolder $OutPath; $OutPath = $null
    }
    [void](Invoke-Fix $FilterPath $LocalExilePath $OutPath $consoleLog)
    return
}

# ---------------------------------------------------------------------------------------------
# Window
# ---------------------------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function Get-FilterCandidates {
    $docs = Get-PoeDocsFolder
    $items = New-Object System.Collections.Generic.List[object]
    if (Test-Path -LiteralPath $docs) {
        Get-ChildItem -LiteralPath $docs -Filter '*.filter' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '* - LocalExile.filter' } |
            ForEach-Object { $items.Add($_) }
        $online = Join-Path $docs 'OnlineFilters'
        if (Test-Path -LiteralPath $online) {
            Get-ChildItem -LiteralPath $online -File -ErrorAction SilentlyContinue | ForEach-Object { $items.Add($_) }
        }
    }
    $Downloads = Join-Path $env:USERPROFILE 'Downloads'
    if (Test-Path -LiteralPath $Downloads) {
        Get-ChildItem -LiteralPath $Downloads -Filter '*.filter' -File -ErrorAction SilentlyContinue | ForEach-Object { $items.Add($_) }
    }
    return @($items | Sort-Object LastWriteTime -Descending)
}

function Get-FilterLabel($file) {
    $name = $file.Name
    try {
        $head = Get-Content -LiteralPath $file.FullName -TotalCount 12 -Encoding UTF8
        $n = $head | Where-Object { $_ -match '^#name:(.+)$' } | Select-Object -First 1
        if ($n) { $name = ($n -replace '^#name:', '').Trim() + "  (online)" }
        $v = $head | Where-Object { $_ -match '^#version:(.+)$' } | Select-Object -First 1
        if ($v) { $name += "  [" + ($v -replace '^#version:', '').Trim() + "]" }
    } catch { }
    $where = Split-Path (Split-Path $file.FullName -Parent) -Leaf
    return ("{0}    -  {1}, {2:yyyy-MM-dd HH:mm}" -f $name, $where, $file.LastWriteTime)
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "LocalExile Filter Fixer v$ToolVersion"
$form.Size = New-Object System.Drawing.Size(820, 660)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$form.MinimumSize = New-Object System.Drawing.Size(640, 480)

$intro = New-Object System.Windows.Forms.Label
$intro.Text = "Makes a FilterBlade / NeverSink filter from a newer patch (e.g. 3.30) load in the LocalExile 3.29 client." +
              " Item names your client doesn't have, and 3.30-only rules, are removed. Your settings and styles stay."
$intro.Location = New-Object System.Drawing.Point(12, 10)
$intro.Size = New-Object System.Drawing.Size(780, 40)
$intro.Anchor = 'Top,Left,Right'
$form.Controls.Add($intro)

$lblLe = New-Object System.Windows.Forms.Label
$lblLe.Text = '1. LocalExile folder (where LocalExile.exe is):'
$lblLe.Location = New-Object System.Drawing.Point(12, 56); $lblLe.AutoSize = $true
$form.Controls.Add($lblLe)

$txtLe = New-Object System.Windows.Forms.TextBox
$txtLe.Location = New-Object System.Drawing.Point(12, 78); $txtLe.Size = New-Object System.Drawing.Size(680, 26)
$txtLe.Anchor = 'Top,Left,Right'
$form.Controls.Add($txtLe)

$btnLe = New-Object System.Windows.Forms.Button
$btnLe.Text = 'Browse...'; $btnLe.Location = New-Object System.Drawing.Point(700, 77); $btnLe.Size = New-Object System.Drawing.Size(92, 28)
$btnLe.Anchor = 'Top,Right'
$form.Controls.Add($btnLe)

$lblF = New-Object System.Windows.Forms.Label
$lblF.Text = '2. Pick the filter to convert (download it from FilterBlade as usual, or sync it to your PoE account):'
$lblF.Location = New-Object System.Drawing.Point(12, 116); $lblF.AutoSize = $true
$form.Controls.Add($lblF)

$list = New-Object System.Windows.Forms.ListBox
$list.Location = New-Object System.Drawing.Point(12, 138); $list.Size = New-Object System.Drawing.Size(680, 150)
$list.Anchor = 'Top,Left,Right'
$list.HorizontalScrollbar = $true
$form.Controls.Add($list)

$btnF = New-Object System.Windows.Forms.Button
$btnF.Text = 'Other file...'; $btnF.Location = New-Object System.Drawing.Point(700, 138); $btnF.Size = New-Object System.Drawing.Size(92, 28)
$btnF.Anchor = 'Top,Right'
$form.Controls.Add($btnF)

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Text = 'Refresh'; $btnRefresh.Location = New-Object System.Drawing.Point(700, 172); $btnRefresh.Size = New-Object System.Drawing.Size(92, 28)
$btnRefresh.Anchor = 'Top,Right'
$form.Controls.Add($btnRefresh)

$lblOut = New-Object System.Windows.Forms.Label
$lblOut.Text = '3. Saves to (the folder the game reads loot filters from):'
$lblOut.Location = New-Object System.Drawing.Point(12, 298); $lblOut.AutoSize = $true
$form.Controls.Add($lblOut)

$txtOut = New-Object System.Windows.Forms.TextBox
$txtOut.Location = New-Object System.Drawing.Point(12, 320); $txtOut.Size = New-Object System.Drawing.Size(680, 26)
$txtOut.Anchor = 'Top,Left,Right'
$txtOut.ReadOnly = $true
$form.Controls.Add($txtOut)

$btnOut = New-Object System.Windows.Forms.Button
$btnOut.Text = 'Change...'; $btnOut.Location = New-Object System.Drawing.Point(700, 319); $btnOut.Size = New-Object System.Drawing.Size(92, 28)
$btnOut.Anchor = 'Top,Right'
$form.Controls.Add($btnOut)

$btnGo = New-Object System.Windows.Forms.Button
$btnGo.Text = '4. Convert for LocalExile'
$btnGo.Location = New-Object System.Drawing.Point(12, 358); $btnGo.Size = New-Object System.Drawing.Size(220, 34)
$btnGo.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($btnGo)

$btnOpen = New-Object System.Windows.Forms.Button
$btnOpen.Text = 'Open filter folder'
$btnOpen.Location = New-Object System.Drawing.Point(242, 358); $btnOpen.Size = New-Object System.Drawing.Size(150, 34)
$form.Controls.Add($btnOpen)

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Multiline = $true; $logBox.ReadOnly = $true; $logBox.ScrollBars = 'Vertical'
$logBox.Location = New-Object System.Drawing.Point(12, 402); $logBox.Size = New-Object System.Drawing.Size(780, 205)
$logBox.Anchor = 'Top,Bottom,Left,Right'
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$form.Controls.Add($logBox)

$guiLog = { param($m) $logBox.AppendText($m + "`r`n"); [System.Windows.Forms.Application]::DoEvents() }

$script:files = @()
function Refresh-List([string]$select) {
    $list.Items.Clear()
    $script:files = @(Get-FilterCandidates)
    if ($select -and -not ($script:files | Where-Object { $_.FullName -eq $select })) {
        $script:files = @(Get-Item -LiteralPath $select) + $script:files
    }
    foreach ($f in $script:files) { [void]$list.Items.Add((Get-FilterLabel $f)) }
    if ($list.Items.Count -gt 0) {
        $idx = 0
        if ($select) { for ($k = 0; $k -lt $script:files.Count; $k++) { if ($script:files[$k].FullName -eq $select) { $idx = $k; break } } }
        $list.SelectedIndex = $idx
    }
}

$btnLe.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select your LocalExile folder'
    if ($txtLe.Text -and (Test-Path -LiteralPath $txtLe.Text)) { $dlg.SelectedPath = $txtLe.Text }
    if ($dlg.ShowDialog() -eq 'OK') {
        $txtLe.Text = $dlg.SelectedPath
        if (-not (Find-TablesFolder $dlg.SelectedPath)) {
            & $guiLog "Warning: that folder has no sql\updates\client item tables. Pick the folder with LocalExile.exe and the sql folder."
        }
    }
})
$btnF.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'Loot filters (*.filter)|*.filter|All files (*.*)|*.*'
    $dlg.InitialDirectory = Join-Path $env:USERPROFILE 'Downloads'
    if ($dlg.ShowDialog() -eq 'OK') { Refresh-List $dlg.FileName }
})
$btnRefresh.Add_Click({ Refresh-List $null })
$btnOpen.Add_Click({
    $d = Get-PoeDocsFolder
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
    Start-Process explorer.exe $d
})
$btnOut.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select Documents\My Games\Path of Exile (the game only reads filters from that folder)'
    if (Test-Path -LiteralPath $txtOut.Text) { $dlg.SelectedPath = $txtOut.Text }
    if ($dlg.ShowDialog() -eq 'OK') {
        $picked = $dlg.SelectedPath
        $resolved = Resolve-PoeFilterFolder $picked
        if ($resolved -ne $picked.TrimEnd('\')) {
            & $guiLog "Using $resolved instead of $picked - the game only reads filters from the 'Path of Exile' folder itself, not its subfolders."
        }
        if ((Split-Path $resolved -Leaf) -ne 'Path of Exile') {
            & $guiLog "WARNING: $resolved isn't a 'My Games\Path of Exile' folder, so the game won't see filters saved there."
        }
        $script:FilterFolder = $resolved
        $txtOut.Text = $resolved
        Save-Settings $txtLe.Text
        Refresh-List $null
    }
})
$btnGo.Add_Click({
    if ($list.SelectedIndex -lt 0) { [System.Windows.Forms.MessageBox]::Show('Pick a filter first.') | Out-Null; return }
    $f = $script:files[$list.SelectedIndex].FullName
    $logBox.Clear()
    $btnGo.Enabled = $false
    $form.Cursor = 'WaitCursor'
    try {
        [void](Invoke-Fix $f $txtLe.Text $null $guiLog)
        Refresh-List $f
    } catch {
        & $guiLog ("ERROR: " + $_.Exception.Message)
    } finally {
        $btnGo.Enabled = $true
        $form.Cursor = 'Default'
    }
})

$form.Add_Shown({
    $le = if ($LocalExilePath) { $LocalExilePath } else { Find-LocalExile }
    $txtOut.Text = Get-PoeDocsFolder
    if ($le) { $txtLe.Text = $le } else { & $guiLog "Couldn't find LocalExile automatically - click Browse and pick the folder with LocalExile.exe." }
    Refresh-List $FilterPath
    if ($FilterPath -and $le) { $btnGo.PerformClick() }
})

[void]$form.ShowDialog()
