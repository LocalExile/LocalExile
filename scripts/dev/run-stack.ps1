$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$base = Split-Path $repo -Parent
$env:PATH = (Join-Path $base 'node\node-v24.4.1-win-x64') + ';' + (Join-Path $base 'dotnet-sdk') + ';' + (Join-Path $base 'python') + ';' + $env:PATH
Set-Location $repo
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\dev\live.ps1') -NoExtract
