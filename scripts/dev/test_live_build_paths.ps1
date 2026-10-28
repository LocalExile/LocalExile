# Run the complete launcher against an isolated checkout and mocked processes.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('live-build-' + [guid]::NewGuid().ToString('N'))
$saved = @{}
foreach ($key in 'Game_CLIENT_DIR', 'Game_BUILD_DIR', 'Game_CLIENT_EXE', 'Game_GGPK', 'Game_GATEWAY_ENTER') {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $null)
}
function Assert($value, $message) { if (-not $value) { throw $message } }
try {
    $repo = Join-Path $fixture 'source with spaces'
    $client = Join-Path $fixture 'client with spaces'
    New-Item -ItemType Directory -Force -Path (Join-Path $repo 'scripts\dev'),
        (Join-Path $repo 'data'), $client | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live.ps1'), (Join-Path $PSScriptRoot 'client-path.ps1') `
        -Destination (Join-Path $repo 'scripts\dev')
    foreach ($file in 'Game.exe', 'Game.patched.exe', 'Content.ggpk') {
        Set-Content -LiteralPath (Join-Path $client $file) -Value 'fixture only'
    }
    Set-Content -LiteralPath (Join-Path $repo 'data\login-keys.json') -Value '{}'
    . (Join-Path $PSScriptRoot 'client-path.ps1')
    $global:liveBuildTestBuilds = [Collections.Generic.List[object]]::new()
    $global:liveBuildTestLaunches = [Collections.Generic.List[object]]::new()
    $global:liveBuildTestStops = 0
    function Get-Process {}
    function Get-CimInstance {}
    function Get-NetTCPConnection {}
    function Stop-Process { $global:liveBuildTestStops++ }
    function Start-Sleep {}
    function dotnet {
        Assert ($args[0] -eq 'build') 'unexpected tool invocation'
        $global:liveBuildTestBuilds.Add(@($args))
        $assembly = Get-ProjectAssembly $repo $args[1] $buildDirectory
        New-Item -ItemType Directory -Force -Path (Split-Path $assembly -Parent) | Out-Null
        Set-Content -LiteralPath $assembly -Value 'fixture DLL'
        $global:LASTEXITCODE = 0
    }
    function Start-Process {
        param($FilePath, $ArgumentList, $WorkingDirectory, $WindowStyle)
        Assert ($FilePath -eq 'cmd.exe') 'unexpected client launch'
        Assert ($WorkingDirectory -eq $repo) 'server working directory moved away from data/'
        Assert ($WindowStyle -eq 'Hidden') 'server helper must stay hidden'
        $global:liveBuildTestLaunches.Add($ArgumentList)
        if ($ArgumentList -match 'GameServer.Patch.dll') {
            Set-Content -LiteralPath (Join-Path $repo 'logs\live-patch.log') -Value 'patching protocol on'
        }
    }
    foreach ($buildDirectory in @($null, (Join-Path $fixture 'build with spaces'))) {
        $config = @{clientDirectory=$client}
        if ($buildDirectory) { $config.buildDirectory=$buildDirectory }
        $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'client.local.json') -Encoding utf8
        foreach ($noBuild in $false, $true) {
            $global:liveBuildTestBuilds.Clear(); $global:liveBuildTestLaunches.Clear()
            & (Join-Path $repo 'scripts\dev\live.ps1') -NoClient -NoExtract -NoBuild:$noBuild
            Assert ($global:liveBuildTestBuilds.Count -eq $(if ($noBuild) { 0 } else { 3 })) 'wrong full build count'
            Assert ($global:liveBuildTestLaunches.Count -eq 3) 'wrong full server count'
            foreach ($s in 'Auth', 'Patch', 'Gateway') {
                $expected = Get-ProjectAssembly $repo "src/GameServer.$s" $buildDirectory
                Assert (@($global:liveBuildTestLaunches | Where-Object { $_.Contains('"' + $expected + '"') }).Count -eq 1) 'wrong DLL or broken space quoting'
            }
            foreach ($call in $global:liveBuildTestBuilds) {
                Assert (($call -contains '--artifacts-path') -eq [bool]$buildDirectory) 'wrong server build layout'
                if ($buildDirectory) { Assert ($call -contains $buildDirectory) 'wrong server artifact directory' }
            }
        }
        foreach ($noBuild in $false, $true) {
            $global:liveBuildTestBuilds.Clear(); $global:liveBuildTestLaunches.Clear()
            & (Join-Path $repo 'scripts\dev\live.ps1') -Only Gateway -NoExtract -NoBuild:$noBuild
            Assert ($global:liveBuildTestBuilds.Count -eq $(if ($noBuild) { 0 } else { 1 })) 'Only Gateway built other servers'
            Assert ($global:liveBuildTestLaunches.Count -eq 1 -and $global:liveBuildTestLaunches[0] -match 'GameServer.Gateway.dll') 'Only Gateway restarted other servers'
        }
    }
    # An old in-source DLL exists from the first run; the external one must not fall back to it.
    $missing = Get-ProjectAssembly $repo 'src/GameServer.Gateway' $buildDirectory
    Assert ($missing.StartsWith($fixture + '\')) 'unexpected fixture DLL path'
    Remove-Item -LiteralPath $missing
    $sentinel = Join-Path $repo 'logs\live-gateway.log'
    Set-Content -LiteralPath $sentinel -Value 'preserve existing logs'
    $global:liveBuildTestLaunches.Clear()
    $failure = $null
    try { & (Join-Path $repo 'scripts\dev\live.ps1') -Only Gateway -NoExtract -NoBuild }
    catch { $failure = $_.Exception.Message }
    Assert ($failure -like "*$missing missing*") 'missing external DLL did not produce a clear error'
    Assert ($global:liveBuildTestLaunches.Count -eq 0 -and $global:liveBuildTestStops -eq 0) 'missing DLL changed running processes'
    Assert ((Get-Content -LiteralPath $sentinel) -eq 'preserve existing logs') 'missing DLL cleared existing logs'
    Write-Output 'Full launcher build/NoBuild/Only Gateway layouts and missing-DLL preflight passed.'
} finally {
    Remove-Variable -Name liveBuildTestBuilds,liveBuildTestLaunches,liveBuildTestStops -Scope Global -ErrorAction SilentlyContinue
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    $resolved = [IO.Path]::GetFullPath($fixture)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^live-build-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
