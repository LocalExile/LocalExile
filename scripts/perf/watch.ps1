# usage: scripts/perf/watch.ps1 [-PortBase 23000] [-IdleSeconds 60]
# What a hot-reload Gateway costs (Agents/docs/dev/performance.md): starts `dotnet watch run` on the Gateway as live.ps1
# does, on PortBase+2/+3/+4 with a copy of data\validation-saves.db and MSBuild node reuse off (the build's worker nodes
# end with it), waits until it listens, then over IdleSeconds records every process of the tree it started: private
# bytes, working set and CPU. It stops that tree only, never a shared compiler server (VBCSCompiler) or MSBuild node.
param([int] $PortBase = 23000, [int] $IdleSeconds = 60, [int] $SettleSeconds = 15)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$out = Join-Path $repo 'artifacts\perf'
New-Item -ItemType Directory -Force $out | Out-Null
$db = Join-Path $out 'watch-saves.db'
Copy-Item (Join-Path $repo 'data\validation-saves.db') $db -Force
$env:Game_SAVES_DB = $db; $env:Game_AUTH_CONTROL_PORT = "$($PortBase + 2)"; $env:Game_GATEWAY_PORT = "$($PortBase + 3)"
$env:Game_DEV_PORT = "$($PortBase + 4)"; $env:MSBUILDDISABLENODEREUSE = '1'
$log = Join-Path $out 'watch.log'
Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
$clock = [Diagnostics.Stopwatch]::StartNew()
$root = Start-Process dotnet -ArgumentList 'watch', 'run', '--project', 'src\GameServer.Gateway', '--non-interactive' -PassThru `
    -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
function Tree([int] $id) {
    $all = @(Get-CimInstance Win32_Process)
    $ids = @($id); $i = 0
    while ($i -lt $ids.Count) { $ids += @($all | Where-Object { $_.ParentProcessId -eq $ids[$i] } | ForEach-Object { $_.ProcessId }); $i++ }
    $all | Where-Object { $ids -contains $_.ProcessId }
}
try {
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
    foreach ($p in @(Tree $root.Id)) {
        if ($p.Name -match 'VBCSCompiler' -or $p.CommandLine -match 'MSBuild\.dll|VBCSCompiler') { continue }
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $db, "$db-wal", "$db-shm" -ErrorAction SilentlyContinue
}
