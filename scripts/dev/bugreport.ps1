# Bundles what a bug report needs into one zip, logs\bugreports\bugreport-<time>.zip (see Agents/docs/dev/debugging.md):
#   - the servers' logs of this run (logs\live-*.log and .err), and with -Previous the run before (logs\archive)
#   - the newest packet dumps (logs\packets: each connection's last messages when it closed, PacketHistory)
#   - the client's own log of this session (Client\logs\LatestClient.txt)
#   - info.txt: the checkout's revision, branch and local changes, the .NET and Windows versions, the Game_* settings
# It works while the servers and the client still run (it reads their logs without locking them).
# Account and character names, machine ids, digests and the Windows user name are replaced by placeholders
# (<account1>, <character1>, …) unless -NoRedact; names also in their hex forms (UTF-8, UTF-16), as packet bytes show
# them. The names are found in the logs (this run's and the archived ones): logins, character creation, tickets, player
# objects, deletions, hideout visits, packet-dump headers. A name the logs never introduce that way is not replaced, so
# read the zip before you attach it anywhere.
#   -Packets <n>   packet dumps to include, newest first (default 5; 0 for none)
#   -Previous      also the previous run's logs
#   -NoRedact      keep the names (for your own use)
param([int]$Packets = 5, [switch]$Previous, [switch]$NoRedact)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logs = Join-Path $repo 'logs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# Byte for byte: the servers write the console's code page, the client UTF-8; Latin-1 reads and writes any byte unchanged.
$latin1 = [Text.Encoding]::GetEncoding(28591)

# A log a running server or client is writing: open it sharing read, write and delete, or Windows refuses.
function Read-Shared([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try {
        $reader = New-Object IO.StreamReader($stream, $latin1)
        $reader.ReadToEnd()
    }
    finally { $stream.Dispose() }
}
# git and dotnet write warnings to stderr; under Stop, Windows PowerShell turns those into errors even with 2>$null.
function Native([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    try { & $command 2>$null } catch { }
}

$files = New-Object System.Collections.Generic.List[object]   # @{ From = <path>; To = <name in the zip> }
# Every live log, whatever size the directory listing shows: a file still open for writing can be listed as empty.
foreach ($f in Get-ChildItem $logs -Filter 'live-*.*' -File -ErrorAction SilentlyContinue) { $files.Add(@{ From = $f.FullName; To = "logs\$($f.Name)" }) }
$archived = @(Get-ChildItem (Join-Path $logs 'archive') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
if ($Previous -and $archived.Count) {
    foreach ($f in Get-ChildItem $archived[0].FullName -File) { $files.Add(@{ From = $f.FullName; To = "logs\previous-$($archived[0].Name)\$($f.Name)" }) }
}
if ($Packets -gt 0) {
    foreach ($f in Get-ChildItem (Join-Path $logs 'packets') -Filter 'packets-*.txt' -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First $Packets) {
        $files.Add(@{ From = $f.FullName; To = "packets\$($f.Name)" })
    }
}
$clientLog = Join-Path $repo 'Client\logs\LatestClient.txt'
if (Test-Path $clientLog) { $files.Add(@{ From = $clientLog; To = 'client\LatestClient.txt' }) }

$texts = [ordered]@{}
foreach ($f in $files) {
    $text = Read-Shared $f.From
    if ($text.Length -gt 0) { $texts[$f.To] = $text }
}
if ($texts.Count -eq 0) { throw "nothing to report: no logs in $logs (run scripts\dev\live.ps1 first)" }

# The names to hide. Packet dumps can be from earlier runs, so the archived logs are searched too (not included).
$replacements = @()   # [pscustomobject]@{ Pattern; With }
if (-not $NoRedact) {
    $search = New-Object System.Collections.Generic.List[string]
    foreach ($t in $texts.Values) { $search.Add($t) }
    foreach ($run in $archived) {
        foreach ($f in Get-ChildItem $run.FullName -Filter 'live-*.log' -File) { $search.Add((Read-Shared $f.FullName)) }
    }
    $all = $search -join "`n"
    $names = { param([string[]]$patterns) $patterns | ForEach-Object { [regex]::Matches($all, $_, 'Multiline') } | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -and $_ -ne '?' } | Sort-Object -Unique -CaseSensitive }
    $accounts = & $names @('(?:Account|AccountName) = "([^"]+)"')
    $characters = & $names @(
        'ticket 0x[0-9a-f]+: (\S+) ',                       # Gateway: the character a ticket admits
        'CreateCharacter \{[^}\r\n]*?Name = "([^"]+)"',    # Auth: character creation
        'PlayerObject \{ Name = ([^,}\s]+)',                 # Gateway: the player object
        '\[auth\] (\S+) deleted;',                           # Auth: deletion
        'visit the hideout of (\S+) ',                       # Gateway: hideout visits
        '^\S+ (\S+) in \S+ seed \d+: ')                      # packet-dump headers
    $hex = { param([string]$s) foreach ($e in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode) { [BitConverter]::ToString($e.GetBytes($s)).Replace('-', '') } }
    $add = {
        param([string]$name, [string]$with)
        # Names are case-sensitive in the game: matching the exact case keeps "player(s)" when an account is "Player".
        if ($name.Length -lt 3) { $script:tooShort++; return }
        $script:replacements += [pscustomobject]@{ Pattern = '(?<![\w])' + [regex]::Escape($name) + '(?![\w])'; With = $with; Length = $name.Length; Options = 'None' }
        foreach ($h in & $hex $name) { $script:replacements += [pscustomobject]@{ Pattern = $h; With = "<$($with.Trim('<>'))-hex>"; Length = $h.Length; Options = 'IgnoreCase' } }
    }
    $tooShort = 0
    $i = 0; foreach ($a in $accounts) { $i++; & $add $a "<account$i>" }
    $i = 0; foreach ($c in $characters) { $i++; & $add $c "<character$i>" }
    if ($env:USERNAME) {
        # The Windows user: in paths whatever the case, elsewhere as written.
        $replacements += [pscustomobject]@{ Pattern = '(?<=\\Users\\)' + [regex]::Escape($env:USERNAME) + '(?=\\|\b)'; With = '<user>'; Length = 1000; Options = 'IgnoreCase' }
        if ($env:USERNAME.Length -ge 3) { $replacements += [pscustomobject]@{ Pattern = '(?<![\w])' + [regex]::Escape($env:USERNAME) + '(?![\w])'; With = '<user>'; Length = $env:USERNAME.Length; Options = 'None' } }
    }
    # Longest first, so a name that contains another is replaced whole.
    $replacements = @($replacements | Sort-Object Length -Descending)
}
function Redact([string]$text) {
    if ($NoRedact) { return $text }
    foreach ($r in $replacements) { $text = [regex]::Replace($text, $r.Pattern, $r.With, $r.Options) }
    $text = [regex]::Replace($text, '(MachineId[AB]|PasswordDigest|Digest) = [^,}]*', '$1 = <redacted>')
    [regex]::Replace($text, 'digest=[0-9A-Fa-f]+', 'digest=<redacted>')
}

$stage = Join-Path ([IO.Path]::GetTempPath()) "localexile-bugreport-$stamp"
try {
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($to in $texts.Keys) {
        $path = Join-Path $stage $to
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        [IO.File]::WriteAllText($path, (Redact $texts[$to]), $latin1)
    }

    $info = New-Object System.Collections.Generic.List[string]
    $info.Add("LocalExile bug report, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    $info.Add('')
    $info.Add("revision: $(Native { git -C $repo rev-parse HEAD })")
    $info.Add("branch:   $(Native { git -C $repo rev-parse --abbrev-ref HEAD })")
    $info.Add("dotnet:   $(Native { dotnet --version })")
    $info.Add("windows:  $([Environment]::OSVersion.VersionString)")
    $info.Add('')
    $info.Add('local changes (git status --short):')
    foreach ($line in @(Native { git -C $repo status --short })) { $info.Add("  $line") }
    $info.Add('')
    $info.Add('last commits:')
    foreach ($line in @(Native { git -C $repo log -5 --oneline })) { $info.Add("  $line") }
    $info.Add('')
    $info.Add('Game_* settings of this shell (live.ps1 gives the servers Game_GATEWAY_ENTER and Game_PACKET_DUMPS itself):')
    foreach ($v in Get-ChildItem env: | Where-Object { $_.Name -like 'Game_*' } | Sort-Object Name) {
        $value = if ($v.Name -match 'SECRET|PASSWORD|PFX|KEY') { '<redacted>' } else { $v.Value }
        $info.Add("  $($v.Name)=$value")
    }
    $info.Add('')
    $info.Add('files:')
    foreach ($to in $texts.Keys) { $info.Add("  $to") }
    [IO.File]::WriteAllText((Join-Path $stage 'info.txt'), (Redact ($info -join "`r`n")) + "`r`n", $latin1)

    $outDir = Join-Path $logs 'bugreports'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $zip = Join-Path $outDir "bugreport-$stamp.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
$redacted = if ($NoRedact) { '' } else { ", $(@($accounts).Count) account(s) and $(@($characters).Count) character(s) redacted" + $(if ($tooShort) { "; $tooShort name(s) under 3 characters left as they are" } else { '' }) }
Write-Output "bug report: $zip ($($texts.Count) file(s)$redacted)"
Write-Output 'Read it before attaching it: the logs show what you did in the game.'
