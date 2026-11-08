# Publishes LocalExileHost.exe (src/LocalExileHost): the host's window around scripts/dev/live.ps1 -Lan.
# Publish it as a FOLDER: a self-extracting single file is what antivirus software deletes.
param([string]$Out = '')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'dist\host' }
Push-Location $repo
try {
    dotnet publish src/LocalExileHost -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=false -p:DebugType=none -o $Out
    if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
    Write-Output "wrote $Out\LocalExileHost.exe (point it at this checkout; it runs scripts\dev\live.ps1 -Lan)"
}
finally { Pop-Location }