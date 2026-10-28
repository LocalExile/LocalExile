# Stop only this checkout's configured client and servers; leave unrelated or unrecognized processes alone.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'client-path.ps1')
$client = Get-ClientPaths $repo
foreach ($process in @(Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue)) {
    if ($process.Path -ne $client.PatchedExecutable) { throw 'Another installation has a client running; it was left alone.' }
    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000)) {
        throw 'The client did not close; close its game window before stopping the servers.'
    }
}
$build = Get-BuildDirectory $repo
$assemblies = @('Auth', 'Patch', 'Gateway', 'Host' | ForEach-Object {
    Get-ProjectAssembly $repo "src\GameServer.$_" $build
    Get-ProjectAssembly $repo "src\GameServer.$_" $null
})
foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'")) {
    if ($process.CommandLine -notmatch 'GameServer\.(Auth|Patch|Gateway|Host)\.dll') { continue }
    $ours = @($assemblies | Where-Object {
        $process.CommandLine.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    if ($ours.Count) {
        Stop-Process -Id $process.ProcessId -Force
        Write-Output "stopped server pid $($process.ProcessId)"
    } else { Write-Output "left unrecognized server pid $($process.ProcessId) running" }
}
