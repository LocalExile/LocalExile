# Unattended decompile of the whole client into Tools/re/out/full (git-ignored). Agents/docs/re/tooling.md section 9.
#   overnight.ps1            starts both tracks detached and returns; rerun the same command to resume
#   overnight.ps1 -Status    what ran, what runs now
#   overnight.ps1 -Stop      ends both tracks and their children; everything is resumable
#   overnight.ps1 -Smoke     the same stages on small limits, into Tools/re/out/full-smoke
# Read-only towards Client/, the shared Ghidra project and the existing decomp-all: it copies the project and never
# starts the client. A stage that fails is retried, then recorded, and the run moves on.
param(
    [ValidateSet('', 'code', 'content')][string]$Track = '',
    [switch]$Status, [switch]$Stop, [switch]$Smoke,
    [int]$ParamIdHours = 4,
    [int]$MinFreeGB = 25,        # below this a track stops
    [int]$ContentFloorGB = 45,   # GGPK extraction stops earlier, leaving room for the code track
    [int]$FinalFloorGB = 30      # the textures' floor once the code track has finished
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$Root = Join-Path $Repo $(if ($Smoke) { 'Tools\re\out\full-smoke' } else { 'Tools\re\out\full' })
$State = "$Root\state"; $Logs = "$Root\logs"
$Headless = "$Repo\Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat"
$Scripts = "$Repo\scripts\re\ghidra"
$Client = "$Repo\Client"
$GameHash = '78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5'
$Limit = if ($Smoke) { 300 } else { 0 }
$env:JAVA_HOME = "$Repo\Tools\jdk-21"

function Say($m) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$Track] $m"
    # Both tracks write here; one that finds the file busy tries again rather than dying.
    for ($i = 0; $i -lt 20; $i++) {
        try { [IO.File]::AppendAllText("$Logs\overnight.log", "$line`r`n"); return } catch { Start-Sleep -Milliseconds 200 }
    }
}

if ($Status -or $Stop) {
    $tracks = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'overnight\.ps1.*-Track' }
    if ($Stop) {
        New-Item -ItemType Directory -Force $State | Out-Null
        New-Item -ItemType File -Force "$State\STOP" | Out-Null
        # Through cmd: under ErrorAction Stop, PowerShell 5.1 turns taskkill's stderr into an error that ends this script.
        foreach ($t in $tracks) { & cmd.exe /d /c "taskkill /T /F /PID $($t.ProcessId) >nul 2>&1" }
        Remove-Item "$State\STOP" -Force
        "stopped $(@($tracks).Count) track(s); rerun overnight.ps1 to resume"
        return
    }
    "running tracks: $(@($tracks).Count)"
    if (Test-Path "$Logs\overnight.log") { Get-Content "$Logs\overnight.log" -Tail 25 }
    if (Test-Path "$Root\REPORT.md") { "report: $Root\REPORT.md" }
    return
}

New-Item -ItemType Directory -Force $State, $Logs | Out-Null

if (-not $Track) {
    $running = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'overnight\.ps1.*-Track' }
    if ($running) { throw "already running (pid $(@($running.ProcessId) -join ', ')); use -Status or -Stop" }
    $hash = (Get-FileHash "$Client\Game.exe" -Algorithm SHA256).Hash
    if ($hash -ne $GameHash) { throw "Client/Game.exe is not the 3.29.3b build the Ghidra project was made from ($hash)" }
    foreach ($t in 'code', 'content') {
        $a = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Track $t -ParamIdHours $ParamIdHours -MinFreeGB $MinFreeGB -ContentFloorGB $ContentFloorGB -FinalFloorGB $FinalFloorGB" + $(if ($Smoke) { ' -Smoke' } else { '' })
        # Created by WMI so the tracks outlive the session that started them.
        $r = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = "powershell.exe $a"; CurrentDirectory = $Repo }
        if ($r.ReturnValue -ne 0) { throw "could not start track $t ($($r.ReturnValue))" }
        "track $t pid $($r.ProcessId)"
    }
    "logs: $Logs\overnight.log"
    return
}

Set-Location $Repo
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'   # children inherit it, so the machine stays usable
Add-Type -Namespace Night -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);'

function Free-GB { [math]::Floor((Get-PSDrive ($Repo.Substring(0, 1))).Free / 1GB) }
function Free-CommitMB { [int]((Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1KB) }

# Runs one command line through cmd with all output in $log. Kills the process tree at the timeout, when the log stops
# growing, or below the memory floor. Returns 'ok' (exit code only; stages check their own results), 'timeout', 'stall',
# 'memory' or 'disk'.
function Invoke-Watched([string]$cmdline, [string]$log, [int]$timeoutMin, [int]$stallMin, [int]$memFloorMB = 0) {
    # A wrapper file: analyzeHeadless.bat breaks on a redirect given in cmd /c's own command line.
    $wrapper = "$log.cmd"
    [IO.File]::WriteAllText($wrapper, "@echo off`r`n$cmdline >> `"$log`" 2>&1`r`n")
    $p = Start-Process cmd.exe -ArgumentList "/d /c `"`"$wrapper`"`"" -NoNewWindow -PassThru
    $null = $p.Handle
    $t0 = Get-Date; $grew = Get-Date; $size = -1; $result = 'ok'
    while (-not $p.WaitForExit(30000)) {
        [void][Night.Power]::SetThreadExecutionState(1)  # ES_SYSTEM_REQUIRED: no idle sleep while this runs
        $now = Get-Date
        $s = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
        if ($s -ne $size) { $size = $s; $grew = $now }
        if (($now - $t0).TotalMinutes -gt $timeoutMin) { $result = 'timeout' }
        elseif (($now - $grew).TotalMinutes -gt $stallMin) { $result = 'stall' }
        elseif ((Free-GB) -lt $MinFreeGB) { $result = 'disk' }
        elseif ($memFloorMB -gt 0 -and (Free-CommitMB) -lt $memFloorMB) { $result = 'memory' }
        if ($result -ne 'ok') { & taskkill /T /F /PID $p.Id 2>$null | Out-Null; break }
    }
    return $result
}

# A stage runs until $check passes: skipped when its .done marker exists, retried up to $retries times.
function Stage([string]$name, [scriptblock]$body, [scriptblock]$check, [int]$retries = 2, [switch]$NoCharge) {
    $done = "$State\$name.done"; $failed = "$State\$name.failed"
    if (Test-Path $done) { Say "$name : already done"; return }
    Remove-Item $failed -ErrorAction SilentlyContinue
    for ($try = 1; $try -le ($retries + 1); $try++) {
        if ((Free-GB) -lt $MinFreeGB) { Say "$name : only $(Free-GB) GB free, stopping this track"; New-Item -ItemType File -Force $failed | Out-Null; Report; exit 4 }
        Say "$name : start (try $try), free $(Free-GB) GB, commit free $(Free-CommitMB) MB"
        $t0 = Get-Date; $r = ''
        try { $r = & $body $try } catch { $r = "exception: $_" }
        $min = [int]((Get-Date) - $t0).TotalMinutes
        $good = $false
        try { $good = [bool](& $check) } catch { Say "$name : check threw: $_" }
        if ($good) { New-Item -ItemType File -Force $done | Out-Null; Say "$name : ok after $min min"; Report; return }
        Say "$name : not complete after $min min ($r)"
        if ($r -eq 'memory') { Start-Sleep -Seconds 600 }
    }
    New-Item -ItemType File -Force $failed | Out-Null
    Say "$name : FAILED, moving on"
    Report
}

function Report([switch]$Index) {
    $a = @('-I', '-X', 'utf8', "$PSScriptRoot\report.py", $Root); if ($Index) { $a += '--index' }
    try { & python @a 2>&1 | Out-Null } catch { Say "report: $_" }
}

function Clear-Locks($dir, $project) { Remove-Item "$dir\$project.lock", "$dir\$project.lock~" -Force -ErrorAction SilentlyContinue }

# One headless run on the Game.exe work copy. A killed run leaves a lock nothing else holds: this copy is ours alone.
function Ghidra-Game([string]$log, [int]$timeoutMin, [int]$stallMin, [string]$heap, [string]$flags, [string]$post) {
    Clear-Locks "$Root\project" 'poe3293'
    $env:GHIDRA_HEADLESS_MAXMEM = $heap
    Invoke-Watched "`"$Headless`" `"$Root\project`" poe3293 -process PathOfExile.exe -noanalysis $flags -scriptPath `"$Scripts`" $post" "$Logs\$log.log" $timeoutMin $stallMin
}

if ($Track -eq 'code') {
    $g = "$Root\game"
    New-Item -ItemType Directory -Force $g | Out-Null

    # Our own copy: other sessions keep using Tools/ghidra-projects, and these passes write to the program.
    Stage 'copy-project' {
        New-Item -ItemType Directory -Force "$Root\project" | Out-Null
        Copy-Item "$Repo\Tools\ghidra-projects\poe3293.gpr" "$Root\project\" -Force
        & robocopy "$Repo\Tools\ghidra-projects\poe3293.rep" "$Root\project\poe3293.rep" /E /NFL /NDL /NJH /NJS /NP /R:3 /W:5 /XF '*.lock' '*.lock~' | Out-Null
        "robocopy $LASTEXITCODE"
    } { Test-Path "$Root\project\poe3293.rep\project.prp" }

    Stage 'game-recover' {
        Ghidra-Game 'game-recover' 120 45 '5G' '' "-postScript NightRecover.java `"$g`""
    } { Test-Path "$g\recover.done" }

    # The deadline is fixed once, so a retry continues the same budget.
    $deadlineFile = "$State\paramid.deadline"
    if (-not (Test-Path $deadlineFile)) { Set-Content $deadlineFile ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + $ParamIdHours * 3600) }
    $deadline = (Get-Content $deadlineFile).Trim()
    $paramArgs = if ($Smoke) { '100 200' } else { '4000' }
    Stage 'game-paramid' {
        Ghidra-Game 'game-paramid' (($ParamIdHours + 1) * 60) 75 '6G' '-max-cpu 6' "-postScript NightParamId.java `"$g`" $deadline $paramArgs"
    } { (Test-Path "$g\paramid.done") -or (Test-Path "$g\paramid.partial") } -retries 4

    Stage 'game-dump' {
        Ghidra-Game 'game-dump' 240 60 '5G' '-readOnly' "-postScript NightDump.java `"$g`" $Limit"
    } { Test-Path "$g\dump.done" }

    Stage 'game-decompile' {
        Ghidra-Game 'game-decompile' 360 45 '5G' '-readOnly' "-postScript DecompAll.java `"$g\decomp`" 4 $Limit"
    } { Test-Path "$g\decomp\index.tsv" } -retries 5

    Stage 'game-retry-failed' {
        # The two biggest functions took 69 and 88 minutes with nothing logged in between.
        Ghidra-Game 'game-retry' 1440 300 '5G' '-readOnly' "-postScript NightRetry.java `"$g\decomp`" 14400"
        Ghidra-Game 'game-reindex' 30 20 '5G' '-readOnly' "-postScript DecompAll.java `"$g\decomp`" 1 $Limit indexonly"
    } { Test-Path "$g\decomp\retry.done" } -retries 1

    Stage 'game-index' { Report -Index; 'ok' } { (Test-Path "$g\index.sqlite") -and (Test-Path "$g\functions.tsv") } -retries 1

    # The client's own programs first, then third-party libraries smallest first; identical files once.
    $own = 'PathOfExile.exe', 'Client.exe', 'PathOfExile_x64.exe', 'Game_x64.exe', 'PackCheck.exe'
    $seen = @{ $GameHash = 1 }
    $seen[(Get-FileHash "$Client\Game.patched.exe" -Algorithm SHA256).Hash] = 1
    $all = Get-ChildItem $Client -Recurse -Depth 2 -Include *.exe, *.dll -File |
        Sort-Object @{ e = { if ($own -contains $_.Name) { 0 } else { 1 } } }, Length
    if ($Smoke) { $all = $all | Where-Object { $_.Name -eq 'Client.exe' } }
    New-Item -ItemType Directory -Force "$Root\project-bins" | Out-Null
    foreach ($bin in $all) {
        $hash = (Get-FileHash $bin.FullName -Algorithm SHA256).Hash
        if ($seen[$hash]) { Say "bin $($bin.Name): same bytes as an earlier file, skipped"; continue }
        $seen[$hash] = 1
        # Client/PathOfExile.exe can be the first bytes of Game.exe: its headers promise 49 MB of code that is not there.
        if ($bin.Length -lt (Get-Item "$Client\Game.exe").Length) {
            $a = [IO.File]::OpenRead($bin.FullName); $b = [IO.File]::OpenRead("$Client\Game.exe")
            $x = New-Object byte[] 65536; $y = New-Object byte[] 65536; $same = $true
            while (($n = $a.Read($x, 0, 65536)) -gt 0) {
                [void]$b.Read($y, 0, $n)
                if ([BitConverter]::ToString($x, 0, $n) -ne [BitConverter]::ToString($y, 0, $n)) { $same = $false; break }
            }
            $a.Close(); $b.Close()
            if ($same) { Say "bin $($bin.Name): only the start of Game.exe, skipped"; continue }
        }
        $d = "$Root\bins\$($bin.Name)"
        Stage "bin-$($bin.Name)" {
            New-Item -ItemType Directory -Force $d | Out-Null
            Clear-Locks "$Root\project-bins" 'clientbins'
            $env:GHIDRA_HEADLESS_MAXMEM = '5G'
            # Up to 2 h of analysis per file, so one huge library cannot take the night.
            Invoke-Watched "`"$Headless`" `"$Root\project-bins`" clientbins -import `"$($bin.FullName)`" -overwrite -analysisTimeoutPerFile 7200 -max-cpu 6 -scriptPath `"$Scripts`" -postScript NightRecover.java `"$d`" -postScript NightDump.java `"$d`" $Limit -postScript DecompAll.java `"$d\decomp`" 4 $Limit" "$Logs\bin-$($bin.Name).log" 300 60
            # NightRetry edits the program in memory, so this has to be its own read-only run. DecompAll again takes
            # anything the first pass missed, and refreshes the index.
            Invoke-Watched "`"$Headless`" `"$Root\project-bins`" clientbins -process `"$($bin.Name)`" -noanalysis -readOnly -scriptPath `"$Scripts`" -postScript NightRetry.java `"$d\decomp`" 3600 -postScript DecompAll.java `"$d\decomp`" 2 $Limit" "$Logs\bin-$($bin.Name)-retry.log" 1440 120
        } { (Test-Path "$d\recover.done") -and (Test-Path "$d\dump.done") -and (Test-Path "$d\decomp\retry.done") } -retries 1
    }
}

if ($Track -eq 'content') {
    $c = "$Root\content"
    $build = "$Root\build"
    New-Item -ItemType Directory -Force $c | Out-Null
    # An artifacts path of our own: a normal build would touch the outputs the watched Gateway depends on.
    Stage 'content-build' {
        Invoke-Watched "dotnet build scripts\re\ggdump\GgDump.csproj -c Release --artifacts-path `"$build`" --nologo -v q" "$Logs\content-build.log" 30 15
    } { Test-Path "$build\bin\GgDump\release\GgDump.exe" }
    $gg = "$build\bin\GgDump\release\GgDump.exe"

    Stage 'content-plan' {
        Invoke-Watched "`"$gg`" `"$Client\Content.ggpk`" `"$c`" plan" "$Logs\content-plan.log" 60 30
    } { Test-Path "$c\extensions.tsv" }

    $limitArg = if ($Smoke) { '500' } else { '' }
    # Models, then audio and the rest, then textures: 94 GB raw that only fit compressed, so they come last, in batches.
    $media = '\.(dds|png|jpg|jpeg|tga|bmp|ogg|wav|mp3|bank|bk2|bik|ttf|otf|header)(\.[a-z0-9]+)?$'
    $text = '\.(datc64|dat64|datl64|dat|ot|otc|it|itc|ao|aoc|act|ams|amd|rs|tsi|tgt|tgr|txt|hlsl|hlsli|fx|fxgraph|json|xml|csv|ini|cfg|lua|epk|pet|trl|mtp|mat|ffx|atlas|arm|tst|tdt|dlp|et|gt|gft|ecf|sm|cht|clt|dct|ddt|env|psg|gmp|red|rrs|ui|smd|fmt)$'
    Stage 'content-text' {
        Invoke-Watched "`"$gg`" `"$Client\Content.ggpk`" `"$c\files`" extract `"$text`" `"$media`" $ContentFloorGB $limitArg" "$Logs\content-text.log" 720 45 2500
    } { (Select-String -Path "$Logs\content-text.log" -Pattern 'extract finished' -Quiet) } -retries 12

    Stage 'content-rest' {
        Invoke-Watched "`"$gg`" `"$Client\Content.ggpk`" `"$c\files`" extract `".`" `"$media`" $ContentFloorGB $limitArg" "$Logs\content-rest.log" 720 45 2500
    } { (Select-String -Path "$Logs\content-rest.log" -Pattern 'extract finished' -Quiet) } -retries 12

    Stage 'content-media' {
        Invoke-Watched "`"$gg`" `"$Client\Content.ggpk`" `"$c\files`" extract `".`" `"\.dds$`" $ContentFloorGB $limitArg" "$Logs\content-media.log" 240 45 2500
    } { (Select-String -Path "$Logs\content-media.log" -Pattern 'extract finished' -Quiet) } -retries 12

    # WOF compression, only when the disk needs it: transparent to readers but slower to read. 1.5:1 measured on textures.
    $CompressBelowGB = 100 + 94
    Stage 'content-compress' {
        if ((Free-GB) -lt $CompressBelowGB) {
            Invoke-Watched "compact /c /s:`"$c\files`" /exe:lzx /i /q" "$Logs\content-compress.log" 360 240
        } else { Say "compress: $(Free-GB) GB free, not needed" }
        New-Item -ItemType File -Force "$State\compress.checked" | Out-Null
    } { Test-Path "$State\compress.checked" } -retries 1

    # Each batch is written raw, and compressed when free space is short. The floor stays high until the code track is done writing, then
    # drops to $FinalFloorGB. Ends in textures.complete, or textures.partial when the disk really is full.
    $tex = "$c\textures"
    Stage 'content-textures' {
        foreach ($n in $(if ($Smoke) { 200 } else { 20000, 40000, 60000, 80000, 100000, 120000, 140000, 0 })) {
            while ($true) {
                $finished = Test-Path "$State\code.finished"
                $floor = if ($finished) { $FinalFloorGB } else { $ContentFloorGB }
                $log = "$Logs\content-textures.log"
                $start = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
                $r = Invoke-Watched "`"$gg`" `"$Client\Content.ggpk`" `"$tex`" extract `"\.dds$`" `"^$`" $floor $(if ($n) { $n })" $log 240 45 2500
                if ((Free-GB) -lt 100) { [void](Invoke-Watched "compact /c /s:`"$tex`" /exe:lzx /i /q *.dds" "$Logs\content-textures-compact.log" 240 120) }
                $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $null = $fs.Seek($start, 'Begin')
                $tail = (New-Object IO.StreamReader $fs).ReadToEnd(); $fs.Close()
                if ($tail -match 'extract finished') { break }
                if ($r -ne 'ok') { return $r }
                if ($tail -notmatch 'stopped: free space') { return 'extract ended early' }
                if ($finished) { New-Item -ItemType File -Force "$State\textures.partial" | Out-Null; Say "textures: disk floor reached at $floor GB"; return 'partial' }
                Say "textures: waiting for the code track before going below $floor GB"
                while (-not (Test-Path "$State\code.finished")) { [void][Night.Power]::SetThreadExecutionState(1); Start-Sleep -Seconds 60 }
            }
        }
        New-Item -ItemType File -Force "$State\textures.complete" | Out-Null
        'ok'
    } { (Test-Path "$State\textures.complete") -or (Test-Path "$State\textures.partial") } -retries 6
}

if ($Track -eq 'code') { New-Item -ItemType File -Force "$State\code.finished" | Out-Null }
Report
Say 'track finished'
