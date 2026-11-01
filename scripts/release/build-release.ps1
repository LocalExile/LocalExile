# Builds a player's release: artifacts\release\LocalExile-v<version>-win-x64.zip and its .sha256 (Agents/docs/dev/releases.md).
# The release workflow runs this; it also runs by hand in a clean checkout (it builds in place: bin\ and obj\ of Release).
# It needs the .NET SDK and npm, and neither the client nor data\: a package never holds game files, and this refuses one that does.
#   -Version <x.y.z[-pre]>   required
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "version '$Version' is not x.y.z or x.y.z-pre" }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo

# Pinned: a new version changes the URL and the hash together. Node's hash is in nodejs.org's SHASUMS256.txt.
$pythonVersion = '3.12.10'
$pythonUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
$pythonSha = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
$nodeVersion = '24.19.0'
$nodeUrl = "https://nodejs.org/dist/v$nodeVersion/node-v$nodeVersion-win-x64.zip"
$nodeSha = '57f71ab3652e797d84acddc79c81cc9ff1c6ddb2a1974cdb83f00fee9bff4c73'

$out = Join-Path $repo 'artifacts\release'
$name = "LocalExile-v$Version-win-x64"
# The zip's one folder has no version in its name: extracting a new zip beside the old folder updates it.
$package = Join-Path $out 'LocalExile'
$downloads = Join-Path $out 'downloads'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Force $package, $downloads | Out-Null

function Step([string]$message) { Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $message" }
function Check-Exit([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }
function Copy-Into([string]$source, [string]$relative) {
    $target = Join-Path $package $relative
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $source $target -Recurse -Force
}
function Get-Pinned([string]$url, [string]$sha, [string]$file) {
    $path = Join-Path $downloads $file
    if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash -ne $sha) {
        Step "downloading $url ..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $progress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest $url -OutFile $path -UseBasicParsing
        $ProgressPreference = $progress
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $sha) { throw "$file does not have the pinned hash" }
    $path
}

$commit = (git rev-parse HEAD).Trim()
Check-Exit 'git rev-parse'
$properties = @('-c', 'Release', "-p:Version=$Version", "-p:InformationalVersion=$Version+$commit", '-v', 'q', '-nologo')

# Modules and mods compile against the Gateway, and copy nothing of it. Each goes into its own bin\ or Mods\Binaries\.
Step 'building the solution in Release ...'
dotnet build GameServer.slnx @properties
Check-Exit 'dotnet build'

# One shared folder, one runtime: the servers and the extraction tools. The tools find Tools\ooz above their own folder.
$app = Join-Path $package 'app'
foreach ($project in 'src\GameServer.Patch', 'src\GameServer.Auth', 'src\GameServer.Gateway',
    'Tools\PoEDataExtractor', 'scripts\re\atlas', 'scripts\re\passive-graphs', 'scripts\re\game-pack', 'scripts\re\ggdump') {
    Step "publishing $project ..."
    dotnet publish $project @properties -r win-x64 --self-contained true -o $app
    Check-Exit "dotnet publish $project"
}

Step 'publishing LocalExile.exe ...'
dotnet publish src\LocalExile @properties -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o $package
Check-Exit 'dotnet publish src\LocalExile'

# The window over LocalExile.exe --json, with Windows' own .NET Framework compiler: no runtime of its own to ship.
Step 'building LocalExileGUI.exe ...'
& powershell -NoProfile -ExecutionPolicy Bypass -File src\LocalExileGui\build.ps1 -Out (Join-Path $package 'LocalExileGUI.exe')
Check-Exit 'src\LocalExileGui\build.ps1'

# The modules as the Gateway loads them: each one's manifest and other files, and its bin\ without the build's leftovers.
Step 'copying modules, mods and leagues ...'
Get-ChildItem Modules -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($repo.Length + 1)
    $relative -notmatch '\\obj\\' -and $_.Extension -notin '.cs', '.csproj', '.props'
} | ForEach-Object { Copy-Into $_.FullName $_.FullName.Substring($repo.Length + 1) }
Copy-Into 'Mods\Binaries' 'Mods\Binaries'
Copy-Into 'Leagues' 'Leagues'
Get-ChildItem (Join-Path $package 'Leagues') -Recurse -Directory -Filter Binaries | Remove-Item -Recurse -Force

# Paths the servers and tools read from their working directory, as in a checkout.
Copy-Into 'data\schema' 'data\schema'
Copy-Into 'scripts\re\levelgen\gate-baseline.json' 'scripts\re\levelgen\gate-baseline.json'
Copy-Into 'Agents\docs\re\components.json' 'Agents\docs\re\components.json'

# The bundled runtimes of the extraction, and the manifest with each entry's program.
Step 'bundling Python and Node ...'
Expand-Archive (Get-Pinned $pythonUrl $pythonSha "python-$pythonVersion.zip") (Join-Path $package 'Tools\python')
# Only these two: the rest is npm, with paths too long for Expand-Archive in Windows PowerShell.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$nodeZip = [IO.Compression.ZipFile]::OpenRead((Get-Pinned $nodeUrl $nodeSha "node-$nodeVersion.zip"))
try {
    foreach ($item in @(@('node.exe', 'Tools\node\node.exe'), @('LICENSE', 'licenses\node-LICENSE.txt'))) {
        $target = Join-Path $package $item[1]
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($nodeZip.GetEntry("node-v$nodeVersion-win-x64/$($item[0])"), $target, $true)
    }
}
finally { $nodeZip.Dispose() }
Push-Location Tools\ooz
npm ci --omit=dev --no-audit --no-fund
Check-Exit 'npm ci'
Pop-Location
Copy-Into 'Tools\ooz\ooz_worker.mjs' 'Tools\ooz\ooz_worker.mjs'
Copy-Into 'Tools\ooz\package.json' 'Tools\ooz\package.json'
Copy-Into 'Tools\ooz\node_modules' 'Tools\ooz\node_modules'

$exported = Join-Path $out 'extract-manifest.exported.json'
& powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev\extract-data.ps1 -ExportManifest $exported
Check-Exit 'extract-data.ps1 -ExportManifest'
# Parsed first: Windows PowerShell's ConvertFrom-Json passes a whole array down a pipeline as one object.
$parsed = Get-Content $exported -Raw | ConvertFrom-Json
$entries = @(foreach ($entry in $parsed) {
    $exe = $null
    $arguments = @($entry.Args)
    if ($entry.Runner -eq 'dotnet') {
        # run --project <folder> [--] <arguments>: the folder's published program, with the arguments.
        $folder = $arguments[2]
        $project = @(Get-ChildItem $folder -Filter *.csproj)
        if ($project.Count -ne 1) { throw "$($entry.Name): no single project in $folder" }
        $exe = "app\$($project[0].BaseName).exe"
        if (-not (Test-Path (Join-Path $package $exe))) { throw "$($entry.Name): $exe was not published" }
        $arguments = @($arguments | Select-Object -Skip 4)
    }
    else {
        foreach ($script in $arguments | Where-Object { $_ -like 'scripts/*.py' }) { Copy-Into $script $script }
    }
    [ordered]@{ Name = $entry.Name; Outputs = @($entry.Outputs); Runner = $entry.Runner; Exe = $exe; Args = $arguments; Version = $entry.Version; Needs = @($entry.Needs) }
})
ConvertTo-Json -Depth 4 -InputObject $entries | Set-Content (Join-Path $package 'Tools\extract-manifest.json') -Encoding ascii
Remove-Item $exported
Copy-Into 'scripts\re\ot_components.py' 'scripts\re\ot_components.py'
Copy-Into 'scripts\re\anim_contacts.py' 'scripts\re\anim_contacts.py'

# Licenses, and what AGPL-3.0 asks: where this exact source is.
Copy-Into 'LICENSE' 'licenses\LICENSE.txt'
Copy-Into 'THIRD-PARTY-NOTICES.md' 'licenses\THIRD-PARTY-NOTICES.md'
Copy-Into 'data\schema\LICENSE' 'licenses\dat-schema-LICENSE.txt'
Copy-Into 'Tools\ooz\node_modules\ooz-wasm\LICENSE' 'licenses\ooz-wasm-LICENSE.txt'
Copy-Item (Join-Path $package 'Tools\python\LICENSE.txt') (Join-Path $package 'licenses\python-LICENSE.txt')
Set-Content (Join-Path $package 'licenses\SOURCE.txt') "LocalExile $Version is built from https://github.com/LocalExile/LocalExile/tree/$commit" -Encoding ascii
Copy-Into 'scripts\release\README-player.txt' 'README.txt'
[ordered]@{ version = $Version; commit = $commit; client = '3.29.3b'; runtime = 'win-x64' } | ConvertTo-Json |
    Set-Content (Join-Path $package 'release.json') -Encoding ascii

# What must hold before anything goes out.
Step 'checking the package ...'
$launcher = Join-Path $package 'LocalExile.exe'
$reported = (& $launcher version).Trim()
if ($reported -ne "$Version+$commit") { throw "LocalExile.exe reports version '$reported'" }
$command = (& $launcher client-command).Trim()
if ($command -cne 'Client\Game.patched.exe --patching-server 127.0.0.1:12995') { throw "the client would start as: $command" }
$bad = @(Get-ChildItem $package -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($package.Length + 1)
    $_.Extension -in '.ggpk', '.bin', '.dat', '.dat64', '.datc64', '.pfx', '.key', '.pem', '.db' -or
    $_.Name -like 'PathOfExile*.exe' -or $_.Name -in 'Game.exe', 'Game.patched.exe', 'Game_x64.exe' -or ($_.Name -like '*secret*' -and $_.Extension -ne '.dll') -or $_.Name -like 'login-keys*' -or
    ($relative -like 'data\*' -and $relative -notlike 'data\schema\*') -or $relative -like 'Client\*'
})
if ($bad.Count) { throw "the package must not hold: $(($bad | Select-Object -First 10 | ForEach-Object { $_.FullName.Substring($package.Length + 1) }) -join ', ')" }
foreach ($required in 'LocalExileGUI.exe', 'app\GameServer.Gateway.exe', 'app\GameServer.Auth.exe', 'app\GameServer.Patch.exe', 'app\GgDump.exe', 'Tools\python\python.exe',
    'Tools\node\node.exe', 'Tools\ooz\node_modules\ooz-wasm\package.json', 'Modules\Core\Game\bin\game.dll', 'scripts\re\levelgen\gate-baseline.json', 'data\schema\schema.min.json') {
    if (-not (Test-Path (Join-Path $package $required))) { throw "the package misses $required" }
}

Step 'compressing ...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$zip.sha256" "$hash  $name.zip" -Encoding ascii
Step "built $zip ($([int]((Get-Item $zip).Length / 1MB)) MB), sha256 $hash"
