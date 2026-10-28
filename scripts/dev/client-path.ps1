# Resolve client files without changing the repository-relative server data directory.
function Get-ClientPaths {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    $directory = $env:Game_CLIENT_DIR
    if (-not $directory) {
        $localConfig = Join-Path $RepositoryRoot 'client.local.json'
        if (Test-Path -LiteralPath $localConfig) {
            try { $config = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json }
            catch { throw "cannot read ${localConfig}: $($_.Exception.Message)" }
            if ($config -isnot [pscustomobject] -or $config.clientDirectory -isnot [string] -or
                [string]::IsNullOrWhiteSpace($config.clientDirectory)) {
                throw "$localConfig must be a JSON object with a nonempty clientDirectory string"
            }
            $directory = $config.clientDirectory
        } else { $directory = 'Client' }
    }
    if ([string]::IsNullOrWhiteSpace($directory)) { throw 'Game_CLIENT_DIR must not be whitespace' }
    function Absolute([string]$path) {
        if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $RepositoryRoot $path }
        [IO.Path]::GetFullPath($path)
    }
    $directory = Absolute $directory
    [pscustomobject]@{
        Directory = $directory
        Executable = if ($env:Game_CLIENT_EXE) { Absolute $env:Game_CLIENT_EXE } else { Join-Path $directory 'Game.exe' }
        PatchedExecutable = Join-Path $directory 'Game.patched.exe'
        Archive = if ($env:Game_GGPK) { Absolute $env:Game_GGPK } else { Join-Path $directory 'Content.ggpk' }
    }
}
