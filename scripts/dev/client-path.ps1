# Resolve development paths without changing repository-relative data/ and logs/.
function Resolve-RepositoryPath([string]$RepositoryRoot, [string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $RepositoryRoot $Path }
    [IO.Path]::GetFullPath($Path)
}

function Get-LocalDirectory([string]$RepositoryRoot, [string]$Setting, [string]$Variable, $Default) {
    $directory = [Environment]::GetEnvironmentVariable($Variable)
    if (-not $directory) {
        $localConfig = Join-Path $RepositoryRoot 'client.local.json'
        if (Test-Path -LiteralPath $localConfig) {
            try { $config = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json }
            catch { throw "cannot read ${localConfig}: $($_.Exception.Message)" }
            if ($config -isnot [pscustomobject] -or
                -not (@($config.PSObject.Properties.Name | Where-Object { $_ -in 'clientDirectory', 'buildDirectory' }).Count)) {
                throw "$localConfig must be a JSON object with clientDirectory and/or buildDirectory strings"
            }
            foreach ($name in 'clientDirectory', 'buildDirectory') {
                if ($config.PSObject.Properties[$name] -and
                    ($config.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($config.$name))) {
                    throw "$localConfig must contain a nonempty $name string"
                }
            }
            $directory = $config.$Setting
        }
        if (-not $directory) { $directory = $Default }
    }
    if ($null -eq $directory) { return $null }
    if ([string]::IsNullOrWhiteSpace($directory)) { throw "$Variable must not be whitespace" }
    Resolve-RepositoryPath $RepositoryRoot $directory
}

function Get-ClientPaths {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    $directory = Get-LocalDirectory $RepositoryRoot 'clientDirectory' 'Game_CLIENT_DIR' 'Client'
    [pscustomobject]@{
        Directory = $directory
        Executable = if ($env:Game_CLIENT_EXE) { Resolve-RepositoryPath $RepositoryRoot $env:Game_CLIENT_EXE } else { Join-Path $directory 'Game.exe' }
        PatchedExecutable = Join-Path $directory 'Game.patched.exe'
        Archive = if ($env:Game_GGPK) { Resolve-RepositoryPath $RepositoryRoot $env:Game_GGPK } else { Join-Path $directory 'Content.ggpk' }
    }
}

function Get-BuildDirectory([string]$RepositoryRoot) {
    Get-LocalDirectory $RepositoryRoot 'buildDirectory' 'Game_BUILD_DIR' $null
}

function Get-DotnetBuildArguments([string]$Project, [string]$BuildDirectory) {
    @('build', $Project, '--configuration', 'Debug')
    if ($BuildDirectory) { @('--artifacts-path', $BuildDirectory) }
}

# These development projects use their project name as AssemblyName and target net10.0.
function Get-ProjectAssembly([string]$RepositoryRoot, [string]$Project, [string]$BuildDirectory) {
    $projectPath = Resolve-RepositoryPath $RepositoryRoot $Project
    $name = Split-Path $projectPath -Leaf
    if ($projectPath.EndsWith('.csproj')) {
        $name = [IO.Path]::GetFileNameWithoutExtension($name)
        $projectPath = Split-Path $projectPath -Parent
    }
    if ($BuildDirectory) { Join-Path $BuildDirectory "bin\$name\debug\$name.dll" }
    else { Join-Path $projectPath "bin\Debug\net10.0\$name.dll" }
}
