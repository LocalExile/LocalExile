# Exercise both script languages against the same isolated checkout; no installed client is touched.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'client-path.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('client-paths-' + [guid]::NewGuid().ToString('N'))
$savedDirectory = $env:Game_CLIENT_DIR
$savedExe = $env:Game_CLIENT_EXE
$savedArchive = $env:Game_GGPK
function Assert($value, $message) { if (-not $value) { throw $message } }
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $env:Game_CLIENT_DIR = $null; $env:Game_CLIENT_EXE = $null; $env:Game_GGPK = $null
    $configPath = Join-Path $testRoot 'client.local.json'
    function Check($expected) {
        $paths = Get-ClientPaths -RepositoryRoot $testRoot
        Assert ($paths.Directory -eq $expected) 'wrong PowerShell directory'
        $module = Join-Path $PSScriptRoot 'client_paths.py'
        $pythonResult = & python -c 'import runpy,sys; print(runpy.run_path(sys.argv[1]).get(sys.argv[3])(sys.argv[2]))' $module $testRoot client_directory
        Assert ($LASTEXITCODE -eq 0 -and $pythonResult -eq $expected) 'Python/PowerShell directory mismatch'
        Assert ($paths.Archive -eq (Join-Path $expected 'Content.ggpk')) 'wrong archive'
    }
    Check (Join-Path $testRoot 'Client')
    '{"clientDirectory":"relative client with spaces"}' | Set-Content -LiteralPath $configPath -Encoding UTF8
    Check (Join-Path $testRoot 'relative client with spaces')
    $external = Join-Path ([IO.Path]::GetTempPath()) 'external client with spaces'
    @{clientDirectory=$external} | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
    Check $external
    $env:Game_CLIENT_DIR = 'environment client'
    Check (Join-Path $testRoot 'environment client')
    $env:Game_CLIENT_EXE = 'separate.exe'; $env:Game_GGPK = 'separate.ggpk'
    $paths = Get-ClientPaths -RepositoryRoot $testRoot
    Assert ($paths.Executable -eq (Join-Path $testRoot 'separate.exe')) 'lost executable override'
    Assert ($paths.Archive -eq (Join-Path $testRoot 'separate.ggpk')) 'lost archive override'
    $env:Game_CLIENT_DIR = $null
    foreach ($invalid in @('{bad', '[]', '{}', '{"clientDirectory":42}', '{"clientDirectory":" "}')) {
        $invalid | Set-Content -LiteralPath $configPath -Encoding UTF8
        $failed = $false
        try { Get-ClientPaths -RepositoryRoot $testRoot | Out-Null } catch { $failed = $true }
        Assert $failed 'invalid config silently fell back'
    }
    Write-Output 'Client path defaults, relative/absolute paths, spaces, precedence and validation passed.'
} finally {
    $env:Game_CLIENT_DIR=$savedDirectory; $env:Game_CLIENT_EXE=$savedExe; $env:Game_GGPK=$savedArchive
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^client-paths-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
