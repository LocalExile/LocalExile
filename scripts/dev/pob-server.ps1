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
# Normal receiver startup belongs to Gateway. This wrapper issues tokens or explicitly starts a standalone listener.
# Token commands need no config file: database/client/league settings come from the normal Game_* environment.
$dll = Join-Path $repo 'src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.dll'
# A token operation can use the running Gateway's build. Rebuilding its loaded DLL would fail on Windows.
if (-not $NoBuild -and (-not ($IssueToken -or $RevokeToken) -or -not (Test-Path -LiteralPath $dll))) {
    & dotnet build src/GameServer.Gateway --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $dll)) { throw 'Gateway is not built. Run this command again without -NoBuild.' }
$operation = if ($IssueToken -or $RevokeToken) { '--pob-token' } else { '--pob-server' }
$arguments = @($dll, $operation)
if ($PSBoundParameters.ContainsKey('Config')) { $arguments += @('--config', $Config) }
if ($Account) { $arguments += @('--account', $Account) }
if ($RevokeToken) { $arguments += '--revoke' }
& dotnet @arguments
exit $LASTEXITCODE
