# Builds LocalExileConnect.exe (src/LocalExileConnect): the one-file setup tool a remote player runs beside their
# client — point the GGPK at the host, put the original back, check the host answers, start the game.
# It is deliberately not part of GameServer.slnx: the servers' build does not need it, and a release only carries it if
# scripts/release/build-release.ps1 is told to (Agents/docs/dev/lan-play.md).
param([string]$Out = '')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'dist\connect' }
Push-Location $repo
try {
    # dotnet from PATH, as live.ps1 does. A machine whose SDK lives in a user profile needs DOTNET_ROOT set first.
    dotnet publish src/LocalExileConnect -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=false -p:DebugType=none -o $Out
    if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
    $exe = Join-Path $Out 'LocalExileConnect.exe'
    Write-Output "wrote $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)"
    Write-Output 'Hand LocalExileConnect.exe to the player; they put it in their client folder (beside Content.ggpk).'
}
finally { Pop-Location }