# What live.ps1 needs from Windows, on Linux. Dot-sourced by live.ps1 only when $IsLinux; Windows never reads this file.
# live.ps1 keeps one path for finding and stopping processes: these two functions answer its Windows queries in the same
# shape, so its matching rules (this checkout's servers, its client, what a run may stop) stay the same on both systems.
# The client runs under Proton through umu-run; the rest of its setup (keys, patch, gateway list) does not change.

# The patched client through umu-run. Settings a shell gives win over these. The prefix holds the client's settings and
# saved login (Documents\My Games\Path of Exile): one prefix of LocalExile's own, so they outlive any install and every
# install has them, and no other Path of Exile ever sees them. Login keys are in data\, so nothing there depends on it.
#   Game_CLIENT_RUNNER  the program that runs a Windows exe (default umu-run)
#   WINEPREFIX          default ~/.local/share/LocalExile/wine-prefix
#   PROTONPATH          default GE-Proton, the newest GE-Proton (umu downloads it on first use)
#   GAMEID              default umu-238960, Path of Exile's protonfixes
$script:ClientStartTimeout = 300    # a first start creates the prefix, and may download Proton and its runtime

# The Linux path of a Windows path a process under Wine shows. Proton picks the drive letters by where the game is
# (X:\Games\... when Z: is the root and X: the home folder), so the letter is read from that process's own prefix.
function ConvertFrom-WinePath([string]$path, [string]$folder) {
    if ($path -notmatch '^(?:\\\\\?\\)?([A-Za-z]):(\\.*)$') { return $path }
    $letter = $Matches[1].ToLowerInvariant(); $rest = $Matches[2].Replace('\', '/')
    try {
        $prefix = [IO.File]::ReadAllText("$folder/environ").Split([char]0) | Where-Object { $_ -like 'WINEPREFIX=*' } | Select-Object -First 1
        if (-not $prefix) { return $path }
        $drive = [IO.File]::ResolveLinkTarget("$($prefix.Substring(11).TrimEnd('/'))/dosdevices/${letter}:", $false)
        "$($drive.FullName.TrimEnd('/'))$rest"
    }
    catch { $path }
}

# The processes of Get-CimInstance Win32_Process, from /proc. Names follow Windows' so that live.ps1's checks read the
# same: a native program is "<file>.exe" (dotnet.exe, GameServer.Gateway.exe), and a process under Wine, which shows
# its Windows path as its first argument, has that file's name and that exe's Linux path.
function Get-LinuxProcesses {
    $bootLine = [IO.File]::ReadAllLines('/proc/stat') | Where-Object { $_ -like 'btime *' }
    $boot = [DateTimeOffset]::FromUnixTimeSeconds([long]($bootLine -split ' ')[1])
    foreach ($folder in [IO.Directory]::GetDirectories('/proc')) {
        $id = [IO.Path]::GetFileName($folder)
        if ($id -notmatch '^\d+$') { continue }
        # A process can end at any point here.
        try {
            $stat = [IO.File]::ReadAllText("$folder/stat")
            $arguments = [IO.File]::ReadAllText("$folder/cmdline").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
        }
        catch { continue }
        if (-not $arguments.Count) { continue }    # a kernel thread, or a process that has ended
        # After the name in parentheses (which may hold spaces): state, parent, ... and the start time, 19 fields on, in
        # the 100ths of a second since boot that every Linux architecture reports here.
        $fields = $stat.Substring($stat.LastIndexOf(')') + 2).Split(' ')
        if ($arguments[0] -match '^[A-Za-z]:\\|\.exe$') {
            $name = ($arguments[0] -split '[\\/]')[-1]
            $path = ConvertFrom-WinePath $arguments[0] $folder
        }
        else {
            $path = try { [IO.File]::ResolveLinkTarget("$folder/exe", $false).FullName } catch { $arguments[0] }
            $name = "$([IO.Path]::GetFileName($path)).exe"
        }
        [pscustomobject]@{
            ProcessId = [int]$id; ParentProcessId = [int]$fields[1]; Name = $name; ExecutablePath = $path
            CommandLine = $arguments -join ' '; CreationDate = $boot.AddSeconds([long]$fields[19] / 100).LocalDateTime
        }
    }
}

# Get-CimInstance Win32_Process, with live.ps1's filters: "Field = 'value'", "Field = value" and "Field like 'value%'".
function Get-CimInstance {
    [CmdletBinding()]
    param([Parameter(Position = 0)][string]$ClassName, [string]$Filter)
    $processes = Get-LinuxProcesses
    if (-not $Filter) { return $processes }
    if ($Filter -notmatch "^(\w+)\s*(=|like)\s*'?([^']*)'?$") { throw "platform-linux: unknown filter: $Filter" }
    $field = $Matches[1]; $operator = $Matches[2]; $value = $Matches[3]
    if ($operator -eq 'like') { $value = $value.Replace('%', '*'); return $processes | Where-Object { "$($_.$field)" -like $value } }
    $processes | Where-Object { "$($_.$field)" -eq $value }
}

# Get-NetTCPConnection -State Listen [-LocalPort N], from ss. OwningProcess is 0 for a socket of another user's process.
function Get-NetTCPConnection {
    [CmdletBinding()]
    param([int]$LocalPort, [string]$State)
    foreach ($line in @(& ss -Hltnp)) {
        $columns = "$line" -split '\s+'
        if ($columns.Count -lt 4 -or $columns[3] -notmatch ':(\d+)$') { continue }
        $port = [int]$Matches[1]
        if ($PSBoundParameters.ContainsKey('LocalPort') -and $port -ne $LocalPort) { continue }
        $owner = if ($line -match 'pid=(\d+)') { [int]$Matches[1] } else { 0 }
        [pscustomobject]@{ LocalPort = $port; OwningProcess = $owner; State = 'Listen' }
    }
}

# Start-Detached's Linux half: the program in its own session, with its output in the files and no handle of this shell,
# so it outlives the script and a caller reading this script through a pipe does not wait for it. sh execs it: the
# process returned is the program itself.
function Start-LinuxDetached([string]$exe, [string[]]$arguments, [string]$log, [string]$err, [string]$folder = $repo) {
    $start = [Diagnostics.ProcessStartInfo]::new('setsid')
    $start.WorkingDirectory = $folder
    $start.UseShellExecute = $false
    foreach ($argument in @('sh', '-c', 'log=$1; err=$2; shift 2; exec "$@" </dev/null >"$log" 2>"$err"', 'sh', $log, $err, $exe) + $arguments) {
        $start.ArgumentList.Add($argument)
    }
    [Diagnostics.Process]::Start($start)
}

# LocalExile's first settings, in the prefix it chose (one the shell names is the player's, and never written): this
# install's from before the prefix was shared, when it has them, and the network mode the server needs. Once the file
# exists nothing here touches it, so what the player sets in game stays. src/LocalExile/Platform.cs does the same.
function Initialize-GameSettings([string]$prefix) {
    $folder = Join-Path $prefix 'drive_c/users/steamuser/Documents/My Games/Path of Exile'
    $settings = Join-Path $folder 'production_Config.ini'
    if (Test-Path -LiteralPath $settings) { return }
    New-Item -ItemType Directory -Force $folder | Out-Null
    $old = Join-Path $repo 'data/wine-prefix/drive_c/users/steamuser/Documents/My Games/Path of Exile'
    foreach ($name in 'production_login.credentials', 'production_Config.ini') {
        $file = Join-Path $old $name
        if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file (Join-Path $folder $name) }
    }
    $text = if (Test-Path -LiteralPath $settings) { [IO.File]::ReadAllText($settings) } else { '' }
    $text = if ($text -match '(?m)^networking_type=') { $text -replace '(?m)^networking_type=[^\r\n]*', 'networking_type=predictive' }
        else { $text.TrimEnd() + "`r`n[NETWORKING]`r`nnetworking_type=predictive`r`n" }
    # The byte-order mark is how the client writes this file.
    [IO.File]::WriteAllText($settings, $text.TrimStart(), [Text.UTF8Encoding]::new($true))
}

# Starts the client and returns its process, in the members Start-Process's result gives Start-Client. The exe runs
# several processes below umu-run, so it is found by what it is; killing it ends Proton's processes with it.
function Start-WineClient([string]$client, [int]$patchPort) {
    $runner = if ($env:Game_CLIENT_RUNNER) { $env:Game_CLIENT_RUNNER } else { 'umu-run' }
    Need (Get-Command $runner -ErrorAction SilentlyContinue) "$runner not found: install umu-launcher, or set Game_CLIENT_RUNNER"
    $prefix = Join-Path ([Environment]::GetFolderPath('LocalApplicationData', 'Create')) 'LocalExile/wine-prefix'
    if (-not $env:WINEPREFIX) { Initialize-GameSettings $prefix }
    $settings = @{ WINEPREFIX = $prefix; PROTONPATH = 'GE-Proton'; GAMEID = 'umu-238960'; PROTON_VERB = 'run' }
    $before = @{}
    foreach ($name in $settings.Keys) {
        $before[$name] = [Environment]::GetEnvironmentVariable($name)
        if (-not $before[$name]) { [Environment]::SetEnvironmentVariable($name, $settings[$name]) }
    }
    New-Item -ItemType Directory -Force ([Environment]::GetEnvironmentVariable('WINEPREFIX')) | Out-Null
    $log = Join-Path $out 'live-client.log'
    $err = Join-Path $out 'live-client.err'
    try { $wrapper = Start-LinuxDetached $runner @((Join-Path $client 'Game.patched.exe'), '--patching-server', "127.0.0.1:$patchPort") $log $err $client }
    finally { foreach ($name in $before.Keys) { [Environment]::SetEnvironmentVariable($name, $before[$name]) } }
    $waitStart = Get-Date
    $script:nextReport = 10
    while ($true) {
        $found = @(Get-OwnClients)
        if ($found.Count) { return [pscustomobject]@{ Id = $found[0].ProcessId; StartTime = $found[0].CreationDate; HasExited = $false; ExitCode = $null } }
        $ended = $wrapper.HasExited
        if ($ended -or ((Get-Date) - $waitStart).TotalSeconds -gt $script:ClientStartTimeout) {
            if (-not $ended) { & kill -TERM "-$($wrapper.Id)" 2>$null }
            throw "the client did not start ($(if ($ended) { "$runner ended" } else { "none within $($script:ClientStartTimeout) s" })): $((Get-Content $err -Tail 5 -ErrorAction SilentlyContinue) -join "`n") (logs\live-client.log, .err)"
        }
        Report-Wait 'the client (a first start sets up Proton)' $waitStart $log
        Start-Sleep -Milliseconds 500
    }
}

# What needs Frida's view of the client, or Windows' job objects and mutex renaming, stops here rather than half working.
function Assert-LinuxSupport([bool]$walkmaps, [bool]$session) {
    if ($walkmaps) { throw '-Walkmaps is not available on Linux yet: attaching Frida to a client under Proton is untested' }
    if ($session) { throw 'named -Session, -Nullifier and -Agent clients are not available on Linux yet: they start through scripts/dev/nullifier.py, which is Windows-only. -NoClient runs the servers.' }
}
