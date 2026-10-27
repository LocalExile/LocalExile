# Starts the Windows item builder (Tools/ItemBuilder). The Gateway must be running with its item tool enabled, which
# live.ps1 does by default (http://127.0.0.1:6113/).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
dotnet build (Join-Path $repo 'Tools\ItemBuilder\ItemBuilder.csproj') -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed: Tools\ItemBuilder' }
$exe = Join-Path $repo 'Tools\ItemBuilder\bin\Debug\net10.0-windows\ItemBuilder.exe'
Start-Process $exe -WorkingDirectory $repo
Write-Output "item builder started: $exe"
