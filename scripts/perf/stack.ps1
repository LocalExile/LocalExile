# usage: scripts/perf/stack.ps1 [-PortBase 23000] [-Configuration Debug] [-SettleSeconds 15] [-IdleSeconds 60] [-Runs 1]
#                               [-Env @{ NAME = 'value' }] [-NoPatch]
# What one idle server stack costs (Agents/docs/dev/performance.md): starts the built Auth, Patch and Gateway on
# PortBase+1 … +6 with a copy of data\validation-saves.db, waits for each to be ready and SettleSeconds more, then over
# IdleSeconds records each process's CPU time, private bytes (its commit) and working set, and stops exactly the processes
# it started. No player connects. Never touches the default ports. Run from the repository root after `dotnet build`
# (or `dotnet build -c Release` with -Configuration Release).
# PortBase: a block of 10 ports of the worktree's own; the defaults of stack.ps1 (23000), watch.ps1 (23010) and
# startup.ps1 (23020) do not overlap. The shell's location and environment are as they were afterwards.
param(
    [int] $PortBase = 23000,
    [string] $Configuration = 'Debug',
    [int] $SettleSeconds = 15,
    [int] $IdleSeconds = 60,
    [int] $Runs = 1,
    [hashtable] $Env = @{},
    [switch] $NoPatch
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repo
try {
$out = Join-Path $repo 'artifacts\perf'
New-Item -ItemType Directory -Force $out | Out-Null
$bin = "bin\$Configuration\net10.0"
$source = Join-Path $repo 'data\validation-saves.db'
if (-not (Test-Path $source)) { throw "no data\validation-saves.db to copy (the perf run never uses data\saves.db)" }

function Wait-Line($proc, $log, $pattern, $timeoutSeconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        if ($proc.HasExited) { throw "process $($proc.Id) exited early; see $log" }
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $clock.Elapsed.TotalMilliseconds }
        Start-Sleep -Milliseconds 20
    }
    throw "timed out waiting for '$pattern' in $log"
}

$db = Join-Path $out 'stack-saves.db'
Copy-Item $source $db -Force
$rows = @()
for ($run = 1; $run -le $Runs; $run++) {
    $started = [ordered]@{}
    $saved = @{}
    $vars = @{ Game_SAVES_DB = $db; Game_LOGIN_PORT = "$($PortBase + 1)"; Game_AUTH_CONTROL_PORT = "$($PortBase + 2)";
               Game_GATEWAY_PORT = "$($PortBase + 3)"; Game_DEV_PORT = "$($PortBase + 4)";
               Game_PATCH_URL = "http://127.0.0.1:$($PortBase + 5)"; Game_PATCH_TCP_PORT = "$($PortBase + 6)" }
    foreach ($k in $Env.Keys) { $vars[$k] = $Env[$k] }
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
    try {
        $servers = @(@{ Name = 'Auth'; Args = 'serve'; Ready = 'login server on tcp' })
        if (-not $NoPatch) { $servers += @{ Name = 'Patch'; Args = ''; Ready = 'gateway_list.txt ->' } }
        $servers += @{ Name = 'Gateway'; Args = ''; Ready = 'listening on' }
        $ready = @{}
        foreach ($s in $servers) {
            $log = Join-Path $out "stack-$($s.Name.ToLower())-$run.log"
            Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
            $arguments = "src\GameServer.$($s.Name)\$bin\GameServer.$($s.Name).dll $($s.Args)".Trim()
            $p = Start-Process dotnet -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
            $started[$s.Name] = $p
            $ready[$s.Name] = [math]::Round((Wait-Line $p $log $s.Ready 120))
        }
        Start-Sleep -Seconds $SettleSeconds
        $before = @{}
        foreach ($name in $started.Keys) { $started[$name].Refresh(); $before[$name] = $started[$name].TotalProcessorTime }
        Start-Sleep -Seconds $IdleSeconds
        foreach ($name in $started.Keys) {
            $p = $started[$name]; $p.Refresh()
            $rows += [pscustomobject]@{ Run = $run; Server = $name; ReadyMs = $ready[$name]
                CpuMsPerMinute = [math]::Round(($p.TotalProcessorTime - $before[$name]).TotalMilliseconds * 60 / $IdleSeconds, 1)
                PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB); WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB); Threads = $p.Threads.Count }
        }
    }
    finally {
        foreach ($p in $started.Values) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit(5000) | Out-Null } }
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}
Remove-Item $db, "$db-wal", "$db-shm" -ErrorAction SilentlyContinue
$rows | Format-Table -AutoSize | Out-String -Width 200
foreach ($run in 1..$Runs) {
    $r = @($rows | Where-Object Run -eq $run)
    "run {0}: stack private {1} MB, working set {2} MB, idle CPU {3} ms per minute" -f $run,
        ($r | Measure-Object PrivateMB -Sum).Sum, ($r | Measure-Object WorkingSetMB -Sum).Sum, ($r | Measure-Object CpuMsPerMinute -Sum).Sum
}
}
finally { Pop-Location }
