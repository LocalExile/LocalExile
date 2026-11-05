param([int]$Port = 5187)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if ((Get-Location).Path -ne $root) { throw "Run from the repository root: $root" }
dotnet build scripts/dev/map-viewer/MapViewer.csproj -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$env:MAP_VIEWER_PORT = "$Port"
dotnet scripts/dev/map-viewer/bin/Debug/net10.0/MapViewer.dll
