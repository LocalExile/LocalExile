# The evryBuild receiver and account-bound import tokens. Run from the repository root.
param(
    [string]$Config = 'config\pob-server.json',
    [string]$Account,
    [switch]$IssueToken,
    [switch]$RevokeToken,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ((Get-Location).Path -ne $repo) { throw "Run this command from $repo." }
if ($IssueToken -and $RevokeToken) { throw 'Choose -IssueToken or -RevokeToken.' }
if (($IssueToken -or $RevokeToken) -and -not $Account) { throw '-Account is required for token commands.' }
if ($Account -and -not ($IssueToken -or $RevokeToken)) { throw 'Use -IssueToken or -RevokeToken with -Account.' }
if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) {
    if ($PSBoundParameters.ContainsKey('Config')) { throw "Configuration file not found: $Config" }
    Copy-Item -LiteralPath (Join-Path $repo 'config\pob-server.example.json') -Destination (Join-Path $repo $Config)
    Write-Host 'Created config\pob-server.json with local defaults. Server paths and listen address can be edited there.'
}
$configPath = (Resolve-Path -LiteralPath $Config).Path
$buildPath = Join-Path $repo 'artifacts\pob-receiver'
$dll = Join-Path $buildPath 'bin\GameServer.Gateway\debug\GameServer.Gateway.dll'
if (-not $NoBuild) {
    & dotnet build src/GameServer.Gateway --artifacts-path $buildPath --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $dll)) { throw 'Receiver is not built. Run this command again without -NoBuild.' }
$operation = if ($IssueToken -or $RevokeToken) { '--pob-token' } else { '--pob-server' }
$arguments = @($dll, $operation, '--config', $configPath)
if ($Account) { $arguments += @('--account', $Account) }
if ($RevokeToken) { $arguments += '--revoke' }
& dotnet @arguments
exit $LASTEXITCODE
