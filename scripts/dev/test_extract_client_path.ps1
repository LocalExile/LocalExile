# Run the actual extraction orchestration with fake child processes and isolated output files.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('extract-path-' + [guid]::NewGuid().ToString('N'))
$saved = $env:Game_CLIENT_DIR
$savedArchive = $env:Game_GGPK
$savedBuild = $env:Game_BUILD_DIR
$savedRuntime = $env:Game_RUNTIME_DIR
$savedSource = $env:Game_SOURCE_ROOT
$global:clientPathTestCalls = [Collections.Generic.List[object]]::new()
function Assert($value, $message) { if (-not $value) { throw $message } }
try {
    foreach ($external in $false, $true) {
    $repo = Join-Path $fixture "repo with spaces $external"
    $runtime = if ($external) { Join-Path $fixture 'runtime with spaces' } else { $repo }
    $buildDirectory = if ($external) { $runtime } else { $null }
    $client = Join-Path $fixture 'external client with spaces'
    New-Item -ItemType Directory -Force -Path (Join-Path $repo 'scripts\dev'), (Join-Path $repo 'config'), (Join-Path $runtime 'data\tables'),
        (Join-Path $runtime 'Tools\ooz\node_modules\ooz-wasm'), (Join-Path $repo 'Tools\ooz'), $client | Out-Null
    foreach ($file in 'package.json', 'package-lock.json') {
        Set-Content -LiteralPath (Join-Path $repo "Tools\ooz\$file") -Value '{}'
    }
    if ($external) {
        @{version=1; sourceRepository=$repo} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $runtime '.evrypath-runtime.json') -Encoding utf8
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'extract-data.ps1'), (Join-Path $PSScriptRoot 'client-path.ps1'), (Join-Path $PSScriptRoot 'setup-runtime.ps1') -Destination (Join-Path $repo 'scripts\dev')
    Set-Content -LiteralPath (Join-Path $client 'Content.ggpk') -Value 'fixture only'
    $env:Game_CLIENT_DIR=$null; $env:Game_GGPK=$null; $env:Game_BUILD_DIR=$null; $env:Game_RUNTIME_DIR=$null; $env:Game_SOURCE_ROOT=$null
    . (Join-Path $PSScriptRoot 'client-path.ps1')
    $config = @{clientDirectory=$client}
    if ($external) { $config.runtimeDirectory=$runtime }
    $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'config\client.local.json') -Encoding utf8
    $global:clientPathTestCalls.Clear()
    $expectedAssembly = Get-ProjectAssembly $repo 'Tools/PoEDataExtractor' $buildDirectory
    function Start-Process {
        param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$NoNewWindow, [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
        $global:clientPathTestCalls.Add(@($FilePath, $ArgumentList, $env:Game_GGPK))
        Assert ($WorkingDirectory -eq $runtime) 'extraction did not use runtime data root'
        Assert ($env:Game_SOURCE_ROOT -eq $repo -and $env:Game_RUNTIME_DIR -eq $runtime) 'tool source/runtime environment missing'
        Set-Content -LiteralPath $RedirectStandardOutput -Value ''
        Set-Content -LiteralPath $RedirectStandardError -Value ''
        if ($ArgumentList -contains 'build') {
            Assert (($ArgumentList -contains '--artifacts-path') -eq [bool]$buildDirectory) 'wrong extraction build layout'
            if ($buildDirectory) { Assert ($ArgumentList -contains ('"' + $buildDirectory + '"')) 'lost artifact path/spaces' }
            New-Item -ItemType Directory -Force -Path (Split-Path $expectedAssembly -Parent) | Out-Null
            Set-Content -LiteralPath $expectedAssembly -Value 'fixture DLL'
        }
        if ($ArgumentList -contains ('"' + $expectedAssembly + '"')) {
            Assert ($ArgumentList -contains ('"' + (Join-Path $client 'Content.ggpk') + '"')) 'dotnet archive argument lost path/spaces'
            Assert ($ArgumentList -contains ('"' + (Join-Path $repo 'data\schema\schema.min.json') + '"')) 'extractor lost source schema'
            Set-Content -LiteralPath (Join-Path $runtime 'data\tables\WorldAreas.json') -Value '[]'
        }
        if ($ArgumentList -contains '-I') {
            Assert ($env:Game_GGPK -eq (Join-Path $client 'Content.ggpk')) 'Python extraction did not inherit configured archive'
            Assert ($ArgumentList -contains '-B') 'isolated Python must not write bytecode in source'
            Set-Content -LiteralPath (Join-Path $runtime 'data\object-types.json') -Value '{}'
        }
        $process = [pscustomobject]@{Handle=1; ExitCode=0}
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {}
        $process
    }
    & (Join-Path $repo 'scripts\dev\extract-data.ps1') -Only tables,object-types
    Assert ($global:clientPathTestCalls.Count -eq 3) 'expected build, dotnet extractor and Python extractor'
    Assert (-not $env:Game_GGPK) 'extraction leaked archive environment override'
    Assert (-not $env:Game_RUNTIME_DIR -and -not $env:Game_SOURCE_ROOT) 'extraction leaked runtime/source environment'
    Assert (Test-Path -LiteralPath (Join-Path $runtime 'data\extract-stamps.json')) 'missing successful extraction stamps'
    }
    Write-Output 'External archive extraction orchestration passed (mocked children, isolated outputs).'
} finally {
    $env:Game_CLIENT_DIR=$saved; $env:Game_GGPK=$savedArchive
    $env:Game_BUILD_DIR=$savedBuild
    $env:Game_RUNTIME_DIR=$savedRuntime; $env:Game_SOURCE_ROOT=$savedSource
    Remove-Variable -Name clientPathTestCalls -Scope Global -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^extract-path-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
