# Run from the repository root. The input file contains a Community Fork export code or saved build XML.
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Account,
    [string]$Name,
    [string]$League = 'Standard',
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ((Get-Location).Path -ne $repo) { throw "Run this command from $repo." }
if (-not $Preview -and (-not $Account -or -not $Name)) {
    throw 'Supply -Account and -Name to create a new character, or -Preview to validate without saving.'
}
if ($File -ne '-' -and -not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Build file not found: $File" }
# Isolated output lets imports run while the Gateway's normal build files are in use by a running server.
$buildPath = Join-Path $repo 'artifacts\pob-import'
& dotnet build src/GameServer.Gateway --artifacts-path $buildPath --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$arguments = @((Join-Path $buildPath 'bin\GameServer.Gateway\debug\GameServer.Gateway.dll'),
    '--import-pob', '--file', $File, '--league', $League)
if ($Preview) { $arguments += '--preview' }
if ($Account) { $arguments += @('--account', $Account) }
if ($Name) { $arguments += @('--name', $Name) }
& dotnet @arguments
exit $LASTEXITCODE
