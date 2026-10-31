# usage: scripts/perf/watch.ps1 [-PortBase 23010] [-IdleSeconds 60]
# What a hot-reload Gateway costs (Agents/docs/dev/performance.md): starts `dotnet watch run` on the Gateway as live.ps1
# does, on PortBase+2/+3/+4 with a copy of data\validation-saves.db and MSBuild node reuse off (the build's worker nodes
# end with it), waits until it listens, then over IdleSeconds records every process of the tree it started: private
# bytes, working set and CPU. It stops that tree only, never a shared compiler server (VBCSCompiler) or MSBuild node.
# The shell's environment and location are as they were afterwards. PortBase: a block of 10 ports of the worktree's own
# (the default does not overlap stack.ps1's or startup.ps1's).
param([int] $PortBase = 23010, [int] $IdleSeconds = 60, [int] $SettleSeconds = 15)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Tree([int] $id) {
    $all = @(Get-CimInstance Win32_Process)
    $ids = @($id); $i = 0
    while ($i -lt $ids.Count) { $ids += @($all | Where-Object { $_.ParentProcessId -eq $ids[$i] } | ForEach-Object { $_.ProcessId }); $i++ }
    $all | Where-Object { $ids -contains $_.ProcessId }
}
$out = Join-Path $repo 'artifacts\perf'
$db = Join-Path $out 'watch-saves.db'
$vars = @{ Game_SAVES_DB = $db; Game_AUTH_CONTROL_PORT = "$($PortBase + 2)"; Game_GATEWAY_PORT = "$($PortBase + 3)"
           Game_DEV_PORT = "$($PortBase + 4)"; MSBUILDDISABLENODEREUSE = '1' }
$saved = @{}
foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
$root = $null
Push-Location $repo
try {
    New-Item -ItemType Directory -Force $out | Out-Null
    Copy-Item (Join-Path $repo 'data\validation-saves.db') $db -Force
    foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    $log = Join-Path $out 'watch.log'
    Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $root = Start-Process dotnet -ArgumentList 'watch', 'run', '--project', 'src\GameServer.Gateway', '--non-interactive' -PassThru `
        -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    while (-not (Select-String -Path $log -Pattern 'listening on' -Quiet -ErrorAction SilentlyContinue)) {
        if ($root.HasExited -or $clock.Elapsed.TotalMinutes -gt 4) { throw "the watched Gateway did not start; see $log" }
        Start-Sleep -Milliseconds 250
    }
    $ready = $clock.Elapsed.TotalSeconds
    Start-Sleep -Seconds $SettleSeconds
    $procs = @(Tree $root.Id | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } | Where-Object { $_ })
    $before = @{}; foreach ($p in $procs) { $before[$p.Id] = $p.TotalProcessorTime }
    Start-Sleep -Seconds $IdleSeconds
    $rows = foreach ($p in $procs) {
        $p.Refresh()
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
        [pscustomobject]@{ Pid = $p.Id; Name = $p.ProcessName; PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB); WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB)
            CpuMsPerMinute = [math]::Round(($p.TotalProcessorTime - $before[$p.Id]).TotalMilliseconds * 60 / $IdleSeconds, 1)
            Command = if ($cmd.Length -gt 90) { $cmd.Substring(0, 90) } else { $cmd } }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 220
    "ready after {0:0.0} s; tree private {1} MB, working set {2} MB, idle CPU {3} ms per minute" -f $ready,
        ($rows | Measure-Object PrivateMB -Sum).Sum, ($rows | Measure-Object WorkingSetMB -Sum).Sum, ($rows | Measure-Object CpuMsPerMinute -Sum).Sum
}
finally {
    if ($root) {
        foreach ($p in @(Tree $root.Id)) {
            if ($p.Name -match 'VBCSCompiler' -or $p.CommandLine -match 'MSBuild\.dll|VBCSCompiler') { continue }
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $db, "$db-wal", "$db-shm" -ErrorAction SilentlyContinue
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    Pop-Location
}
