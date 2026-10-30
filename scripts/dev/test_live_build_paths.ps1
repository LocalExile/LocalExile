# Run the complete launcher against an isolated checkout and mocked processes.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('live-build-' + [guid]::NewGuid().ToString('N'))
$saved = @{}
foreach ($key in 'Game_CLIENT_DIR', 'Game_BUILD_DIR', 'Game_CLIENT_EXE', 'Game_GGPK', 'Game_GATEWAY_ENTER', 'Game_RUNTIME_DIR', 'Game_SOURCE_ROOT') {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, $null)
}
function Assert($value, $message) { if (-not $value) { throw $message } }
try {
    $repo = Join-Path $fixture 'source with spaces'
    $client = Join-Path $fixture 'client with spaces'
    New-Item -ItemType Directory -Force -Path (Join-Path $repo 'scripts\dev'),
        (Join-Path $repo 'data'), (Join-Path $repo 'config'), $client | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'live.ps1'), (Join-Path $PSScriptRoot 'client-path.ps1'),
        (Join-Path $PSScriptRoot 'setup-runtime.ps1'), (Join-Path $PSScriptRoot 'run.ps1'), (Join-Path $PSScriptRoot 'stop.ps1') `
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
    function Get-CimInstance { $global:liveBuildTestCimTargets }
    function Get-NetTCPConnection {}
    function Stop-Process {
        param($Id, [switch]$Force)
        Assert ($Id -in 97, 99, 101) 'stop helper targeted another installation'
        $global:liveBuildTestStops++
    }
    function Start-Sleep {}
    function dotnet {
        if ($args[0] -like '*.dll') {
            Assert ($args[0] -eq (Get-ProjectAssembly $repo 'src/GameServer.Patch' $buildDirectory)) 'wrong maintenance DLL'
            Assert ($args[1] -eq 'gateway') 'maintenance arguments changed'
            Assert ((Get-Location).Path -eq $runtime) 'maintenance command used source data'
            Assert ($env:Game_SOURCE_ROOT -eq $repo -and $env:Game_RUNTIME_DIR -eq $runtime) 'maintenance tool environment missing'
            $global:liveBuildTestMaintenance++
            $global:LASTEXITCODE = 0
            return
        }
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
        Assert ($WorkingDirectory -eq $runtime) 'server working directory differs from runtime data/'
        Assert ($WindowStyle -eq 'Hidden') 'server helper must stay hidden'
        $global:liveBuildTestLaunches.Add($ArgumentList)
        if ($ArgumentList -match 'GameServer.Patch.dll') {
            Set-Content -LiteralPath (Join-Path $runtime 'logs\live-patch.log') -Value 'patching protocol on'
        }
        if ($ArgumentList -match ' watch run ') {
            Assert ($env:Game_HOT_RELOAD_LOG -eq (Join-Path $runtime 'logs\live-gateway.err')) 'hot reload reads the wrong log'
            Assert ($env:Game_SOURCE_ROOT -eq $repo) 'a watched Gateway cannot find the source tree'
            Set-Content -LiteralPath (Join-Path $runtime 'logs\live-gateway.log') -Value '[gateway] listening on 127.0.0.1:6112'
        }
    }
    foreach ($externalRuntime in $false, $true) {
    $runtime = if ($externalRuntime) { Join-Path $fixture 'runtime with spaces' } else { $repo }
    if ($externalRuntime) {
        $key = Join-Path $repo 'data\login-keys.json'
        Assert ($key.StartsWith($fixture + '\')) 'unexpected fixture data path'
        Remove-Item -LiteralPath $key
        New-Item -ItemType Directory -Force -Path (Join-Path $runtime 'data') | Out-Null
        Set-Content -LiteralPath (Join-Path $runtime 'data\login-keys.json') -Value '{}'
        @{version=1; sourceRepository=$repo} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $runtime '.evrypath-runtime.json') -Encoding utf8
    }
    foreach ($configuredBuild in @($null, (Join-Path $fixture 'build with spaces'))) {
        $config = @{clientDirectory=$client}
        if ($externalRuntime) { $config.runtimeDirectory=$runtime }
        if ($configuredBuild) { $config.buildDirectory=$configuredBuild }
        $buildDirectory = if ($configuredBuild) { $configuredBuild } elseif ($externalRuntime) { $runtime } else { $null }
        $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'config\client.local.json') -Encoding utf8
        foreach ($noBuild in $false, $true) {
            $global:liveBuildTestBuilds.Clear(); $global:liveBuildTestLaunches.Clear()
            & (Join-Path $repo 'scripts\dev\live.ps1') -NoClient -NoExtract -NoBuild:$noBuild -NoHotReload
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
            & (Join-Path $repo 'scripts\dev\live.ps1') -Only Gateway -NoExtract -NoBuild:$noBuild -NoHotReload
            Assert ($global:liveBuildTestBuilds.Count -eq $(if ($noBuild) { 0 } else { 1 })) 'Only Gateway built other servers'
            Assert ($global:liveBuildTestLaunches.Count -eq 1 -and $global:liveBuildTestLaunches[0] -match 'GameServer.Gateway.dll') 'Only Gateway restarted other servers'
        }
        # Hot reload is the default: the watched Gateway builds itself into the selected layout.
        foreach ($noBuild in $false, $true) {
            $global:liveBuildTestBuilds.Clear(); $global:liveBuildTestLaunches.Clear()
            & (Join-Path $repo 'scripts\dev\live.ps1') -NoClient -NoExtract -NoBuild:$noBuild
            Assert ($global:liveBuildTestBuilds.Count -eq $(if ($noBuild) { 0 } else { 2 })) 'the launcher built the watched Gateway'
            Assert ($global:liveBuildTestLaunches.Count -eq 3) 'wrong watched server count'
            $watched = @($global:liveBuildTestLaunches | Where-Object { $_ -match ' watch run ' })
            Assert ($watched.Count -eq 1 -and $watched[0].Contains('--project "' + (Join-Path $repo 'src\GameServer.Gateway') + '"')) 'wrong watched project or broken space quoting'
            Assert ($watched[0] -notmatch 'GameServer.Gateway.dll') 'the Gateway started twice'
            Assert ($watched[0].Contains('--artifacts-path') -eq [bool]$buildDirectory) 'wrong watched build layout'
            if ($buildDirectory) { Assert ($watched[0].Contains('"' + $buildDirectory + '"')) 'wrong watched artifact directory' }
        }
    }
    }
    # An old in-source DLL exists from the first run; the external one must not fall back to it.
    $missing = Get-ProjectAssembly $repo 'src/GameServer.Gateway' $buildDirectory
    Assert ($missing.StartsWith($fixture + '\')) 'unexpected fixture DLL path'
    Remove-Item -LiteralPath $missing
    $sentinel = Join-Path $runtime 'logs\live-gateway.log'
    Set-Content -LiteralPath $sentinel -Value 'preserve existing logs'
    $global:liveBuildTestLaunches.Clear()
    $failure = $null
    try { & (Join-Path $repo 'scripts\dev\live.ps1') -Only Gateway -NoExtract -NoBuild -NoHotReload }
    catch { $failure = $_.Exception.Message }
    Assert ($failure -like "*$missing missing*") 'missing external DLL did not produce a clear error'
    Assert ($global:liveBuildTestLaunches.Count -eq 0 -and $global:liveBuildTestStops -eq 0) 'missing DLL changed running processes'
    Assert ((Get-Content -LiteralPath $sentinel) -eq 'preserve existing logs') 'missing DLL cleared existing logs'
    # A watched Gateway builds itself, so its missing DLL is not a missing setup step.
    & (Join-Path $repo 'scripts\dev\live.ps1') -Only Gateway -NoExtract -NoBuild
    Assert ($global:liveBuildTestLaunches.Count -eq 1 -and $global:liveBuildTestLaunches[0] -match ' watch run ') 'NoBuild refused a watched Gateway'
    $global:liveBuildTestMaintenance=0
    & (Join-Path $repo 'scripts\dev\run.ps1') Patch -NoBuild gateway
    Assert ($global:liveBuildTestMaintenance -eq 1) 'maintenance wrapper did not execute'
    Assert (-not $env:Game_RUNTIME_DIR -and -not $env:Game_SOURCE_ROOT) 'maintenance wrapper leaked environment'
    $auth = Get-ProjectAssembly $repo 'src/GameServer.Auth' $buildDirectory
    $global:liveBuildTestCimTargets = @(
        [pscustomobject]@{ProcessId=99; CommandLine=('dotnet "' + $auth + '" serve')},
        [pscustomobject]@{ProcessId=98; CommandLine='dotnet F:\AnotherInstallation\GameServer.Auth.dll serve'},
        # A watched Gateway: without these `dotnet watch` keeps it listening after a stop.
        [pscustomobject]@{ProcessId=97; CommandLine=('dotnet watch run --project "' + (Join-Path $repo 'src\GameServer.Gateway') + '" --non-interactive --')},
        [pscustomobject]@{ProcessId=96; CommandLine='dotnet watch run --project F:\AnotherInstallation\src\GameServer.Gateway --non-interactive --'},
        [pscustomobject]@{ProcessId=101; CommandLine=('"' + [IO.Path]::ChangeExtension((Get-ProjectAssembly $repo 'src/GameServer.Gateway' $buildDirectory), '.exe') + '"')},
        [pscustomobject]@{ProcessId=95; CommandLine='"F:\AnotherInstallation\GameServer.Gateway.exe"'}
    )
    & (Join-Path $repo 'scripts\dev\stop.ps1')
    Assert ($global:liveBuildTestStops -eq 3) 'stop helper did not select only this runtime'
    Write-Output 'Full launcher build/NoBuild/Only Gateway layouts and missing-DLL preflight passed.'
} finally {
    Remove-Variable -Name liveBuildTestBuilds,liveBuildTestLaunches,liveBuildTestStops,liveBuildTestCimTargets,liveBuildTestMaintenance -Scope Global -ErrorAction SilentlyContinue
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    $resolved = [IO.Path]::GetFullPath($fixture)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^live-build-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
