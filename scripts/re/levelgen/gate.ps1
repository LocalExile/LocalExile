# usage: scripts/re/levelgen/gate.ps1 [-Save] [-NoTests] [gate options: --only <area regex> --no-oracle --jobs N]
# The level generator's check before a commit: levelgen's gate (every seed-1 trace and every logged (area, seed),
# compared with scripts/re/levelgen/gate-baseline.json; Gate.cs) and the three test suites. -Save makes this run the
# baseline (commit gate-baseline.json with the change). Exit code 1 when the gate lost anything or a test failed.
param([switch]$Save, [switch]$NoTests, [Parameter(ValueFromRemainingArguments = $true)][string[]]$GateArgs)
# Not 'Stop': Windows PowerShell turns a native program's stderr into errors. Exit codes count.
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Push-Location $root
try {
    $gate = @('gate') + @($GateArgs | Where-Object { $_ })
    if ($Save) { $gate += '--save' }
    $env:LEVELGEN_LOCAL = '1'
    & (Join-Path $PSScriptRoot 'lg.ps1') @gate
    $gateCode = $LASTEXITCODE
    Remove-Item Env:LEVELGEN_LOCAL
    $failed = @()
    if (-not $NoTests) {
        # The Gateway's own bin/ is locked while the Gateway server runs: build its tests elsewhere.
        $suites = @(
            @('tests/GameServer.Core.Tests'),
            @('tests/GameServer.Data.Tests'),
            @('tests/GameServer.Gateway.Tests', '--artifacts-path', 'scripts/re/levelgen/bin/gwtest'))
        foreach ($s in $suites) {
            $lines = & dotnet test @s 2>&1
            $summary = $lines | Select-String -Pattern '(Passed|Failed)!' | Select-Object -Last 1
            Write-Output "  $($s[0]): $(if ($summary) { $summary.Line.Trim() } else { 'no summary (build failed?)' })"
            if ($LASTEXITCODE -ne 0) { $failed += $s[0] }
        }
    }
    if ($gateCode -ne 0 -or $failed.Count -gt 0) {
        Write-Output "gate: FAILED$(if ($gateCode -ne 0) { ' (regressions above)' })$(if ($failed) { ' (tests: ' + ($failed -join ', ') + ')' })"
        exit 1
    }
    Write-Output 'gate: passed'
}
finally { Pop-Location }
