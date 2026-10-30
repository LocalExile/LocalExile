# Config-aware foreground entry point for maintenance commands and individual server projects.
param(
    [Parameter(Mandatory = $true)][ValidateSet('Auth', 'Patch', 'Gateway', 'Host', 'DevClient')][string]$Project,
    [switch]$NoBuild,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'client-path.ps1')
$runtime = Get-RuntimeDirectory $repo
$build = Get-BuildDirectory $repo
$client = Get-ClientPaths $repo
& (Join-Path $PSScriptRoot 'setup-runtime.ps1')
$projectPath = Join-Path $repo "src\GameServer.$Project"
if (-not $NoBuild) {
    $buildArgs = @(Get-DotnetBuildArguments $projectPath $build)
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed: GameServer.$Project" }
}
$assembly = Get-ProjectAssembly $repo $projectPath $build
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "$assembly missing; run without -NoBuild first" }
$settings = @{ Game_SOURCE_ROOT=$repo; Game_RUNTIME_DIR=$runtime
    Game_CLIENT_EXE=$client.Executable; Game_GGPK=$client.Archive }
$saved = @{}
foreach ($key in $settings.Keys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $settings[$key])
}
Push-Location $runtime
try { & dotnet $assembly @Arguments; $code=$LASTEXITCODE }
finally {
    Pop-Location
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
}
exit $code
