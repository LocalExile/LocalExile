# usage: scripts/re/levelgen/lg.ps1 <levelgen command> …
# Runs levelgen (scripts/re/levelgen/Program.cs) without `dotnet run`'s ~10 s: builds into bin/lg only when a source
# under scripts/re/levelgen or src/GameServer.Data is newer than the build, then runs the dll, which hands the command to
# the levelgen server (`serve`, started on first use, replaced after a rebuild) so the game files stay open.
# LEVELGEN_LOCAL=1 runs without the server.
# Not 'Stop': Windows PowerShell turns a native program's stderr (levelgen's progress lines) into errors. Exit codes count.
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$out = Join-Path $PSScriptRoot 'bin\lg'
$dll = Join-Path $out 'LevelGen.dll'

$sources = @(Get-ChildItem -Path $PSScriptRoot -File -Filter '*.cs') + @(Get-ChildItem -Path $PSScriptRoot -File -Filter '*.csproj') +
    @(Get-ChildItem -Path (Join-Path $root 'src\GameServer.Data') -Recurse -File -Include '*.cs', '*.csproj' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$newest = ($sources | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
if (-not (Test-Path $dll) -or (Get-Item $dll).LastWriteTimeUtc -lt $newest) {
    $build = & dotnet build (Join-Path $PSScriptRoot 'LevelGen.csproj') -o $out -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-String -Pattern 'error' | ForEach-Object { $_.Line }; exit 1 }
}
& dotnet $dll @args
exit $LASTEXITCODE
