# Run the actual extraction orchestration with fake child processes and isolated output files.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('extract-path-' + [guid]::NewGuid().ToString('N'))
$saved = $env:Game_CLIENT_DIR
$savedArchive = $env:Game_GGPK
$savedBuild = $env:Game_BUILD_DIR
$global:clientPathTestCalls = [Collections.Generic.List[object]]::new()
function Assert($value, $message) { if (-not $value) { throw $message } }
try {
    $repo = Join-Path $fixture 'repo with spaces'
    $client = Join-Path $fixture 'external client with spaces'
    New-Item -ItemType Directory -Force -Path (Join-Path $repo 'scripts\dev'), (Join-Path $repo 'data\tables'),
        (Join-Path $repo 'Tools\ooz\node_modules\ooz-wasm'), $client | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'extract-data.ps1'), (Join-Path $PSScriptRoot 'client-path.ps1') -Destination (Join-Path $repo 'scripts\dev')
    Set-Content -LiteralPath (Join-Path $client 'Content.ggpk') -Value 'fixture only'
    $env:Game_CLIENT_DIR=$null; $env:Game_GGPK=$null; $env:Game_BUILD_DIR=$null
    . (Join-Path $PSScriptRoot 'client-path.ps1')
    foreach ($buildDirectory in @($null, (Join-Path $fixture 'external build with spaces'))) {
    $config = @{clientDirectory=$client}
    if ($buildDirectory) { $config.buildDirectory=$buildDirectory }
    $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'client.local.json') -Encoding utf8
    $global:clientPathTestCalls.Clear()
    $expectedAssembly = Get-ProjectAssembly $repo 'Tools/PoEDataExtractor' $buildDirectory
    function Start-Process {
        param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$NoNewWindow, [switch]$PassThru, $RedirectStandardOutput, $RedirectStandardError)
        $global:clientPathTestCalls.Add(@($FilePath, $ArgumentList, $env:Game_GGPK))
        Assert ($WorkingDirectory -eq $repo) 'extraction changed server-data root'
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
            Set-Content -LiteralPath (Join-Path $repo 'data\tables\WorldAreas.json') -Value '[]'
        }
        if ($ArgumentList -contains '-I') {
            Assert ($env:Game_GGPK -eq (Join-Path $client 'Content.ggpk')) 'Python extraction did not inherit configured archive'
            Set-Content -LiteralPath (Join-Path $repo 'data\object-types.json') -Value '{}'
        }
        $process = [pscustomobject]@{Handle=1; ExitCode=0}
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {}
        $process
    }
    & (Join-Path $repo 'scripts\dev\extract-data.ps1') -Only tables,object-types
    Assert ($global:clientPathTestCalls.Count -eq 3) 'expected build, dotnet extractor and Python extractor'
    Assert (-not $env:Game_GGPK) 'extraction leaked archive environment override'
    Assert (Test-Path -LiteralPath (Join-Path $repo 'data\extract-stamps.json')) 'missing successful extraction stamps'
    }
    Write-Output 'External archive extraction orchestration passed (mocked children, isolated outputs).'
} finally {
    $env:Game_CLIENT_DIR=$saved; $env:Game_GGPK=$savedArchive
    $env:Game_BUILD_DIR=$savedBuild
    Remove-Variable -Name clientPathTestCalls -Scope Global -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^extract-path-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
