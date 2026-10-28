# Run from the source checkout. Normal startup initializes folders; -Migrate preserves an existing local installation.
param([switch]$Migrate, [switch]$Dependencies, [switch]$PythonTools)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'client-path.ps1')
$runtime = Get-RuntimeDirectory $repo
$marker = Join-Path $runtime '.evrypath-runtime.json'

function Assert-OrdinaryPath([string]$Path, [switch]$Tree) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($part = $full; $part; $part = Split-Path $part -Parent) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Runtime setup refuses links/junctions: $part"
            }
        }
    }
    if ($Tree -and (Test-Path -LiteralPath $full -PathType Container)) {
        $links = @(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object {
            $_.Attributes -band [IO.FileAttributes]::ReparsePoint
        })
        if ($links.Count) { throw "Runtime setup refuses links/junctions under $full" }
    }
}
function File-Manifest([string]$Path) {
    $items = if (Test-Path -LiteralPath $Path -PathType Container) {
        @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force)
    } else { @(Get-Item -LiteralPath $Path -Force) }
    $result = @{}
    foreach ($item in $items) {
        $key = if ($item.FullName -eq $Path) { '.' } else { $item.FullName.Substring($Path.Length + 1) }
        $result[$key] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
    return $result
}
function Assert-Manifests($Expected, $Actual, [string]$Label) {
    if ($Expected.Count -ne $Actual.Count) { throw "File count mismatch: $Label" }
    foreach ($key in $Expected.Keys) {
        if ($Expected[$key] -ne $Actual[$key]) { throw "SHA256 mismatch: $Label / $key" }
    }
}

if ($runtime -ne $repo) {
    $sourcePrefix = $repo.TrimEnd('\') + '\'
    $runtimePrefix = $runtime.TrimEnd('\') + '\'
    if ($runtime.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $repo.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An external runtime must be separate from the source checkout, not inside or above it.'
    }
    $client = (Get-ClientPaths $repo).Directory.TrimEnd('\')
    if ($runtime -eq $client -or $runtime.StartsWith($client + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $client.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime and client folders must be separate.'
    }
    # Check ancestors now; recursive checks are limited to folders the migration actually handles.
    for ($part = $runtime; $part; $part = Split-Path $part -Parent) {
        if ((Test-Path -LiteralPath $part) -and
            ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Runtime must use ordinary directories: $part"
        }
    }
    $legacyData = @(Get-ChildItem -LiteralPath (Join-Path $repo 'data') -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'schema' })
    if (Test-Path -LiteralPath $marker) {
        $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($owner.sourceRepository -ne $repo -or $owner.version -ne 1) {
            throw "$runtime belongs to another checkout or an unsupported runtime format"
        }
        if ($legacyData.Count) { throw 'Runtime data reappeared in the source checkout; reconcile it before starting servers.' }
        if ($Migrate) { throw 'This runtime is already initialized; migration will not overwrite it.' }
    } elseif ($Migrate) {
        $running = @(Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'Game.patched.exe' -or ($_.Name -eq 'dotnet.exe' -and
                $_.CommandLine -match 'GameServer\.(Auth|Patch|Gateway|Host)\.dll')
        })
        if ($running.Count) { throw 'Close the client and stop local GameServer processes before migrating runtime files.' }
        $backup = Join-Path (Split-Path $repo -Parent) ('runtime-migration-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        if (Test-Path -LiteralPath $backup) { throw "Backup destination exists: $backup" }
        Assert-OrdinaryPath (Split-Path $backup -Parent)
        $jobs = [Collections.Generic.List[object]]::new()
        foreach ($entry in $legacyData) {
            $jobs.Add(@{ source=$entry.FullName; relative=('data\' + $entry.Name); copy=$true })
        }
        foreach ($relative in 'logs', 'Tools\re\out\ot', 'Tools\ooz\node_modules') {
            $path = Join-Path $repo $relative
            if (Test-Path -LiteralPath $path) { $jobs.Add(@{ source=$path; relative=$relative; copy=$true }) }
        }
        # Preserve obsolete in-source build output outside the source tree, without mixing it into the new build.
        $projects = @(& git -C $repo ls-files '*.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory source project build folders.' }
        foreach ($project in $projects) {
            foreach ($folder in 'bin', 'obj') {
                $relative = Join-Path (Split-Path $project -Parent) $folder
                $path = Join-Path $repo $relative
                if (Test-Path -LiteralPath $path) { $jobs.Add(@{ source=$path; relative=$relative; copy=$false }) }
            }
        }
        foreach ($job in $jobs) {
            Assert-OrdinaryPath $job.source -Tree
            if ($job.copy) {
                $destination = Join-Path $runtime $job.relative
                Assert-OrdinaryPath $destination
                if (Test-Path -LiteralPath $destination) { throw "Migration refuses to overwrite $destination" }
            }
        }
        New-Item -ItemType Directory -Path $backup | Out-Null
        $moved = [Collections.Generic.List[object]]::new()
        try {
            foreach ($job in $jobs) {
                $job.hashes = File-Manifest $job.source
                if ($job.copy) {
                    $destination = Join-Path $runtime $job.relative
                    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
                    Copy-Item -LiteralPath $job.source -Destination $destination -Recurse -Force
                    Assert-Manifests $job.hashes (File-Manifest $destination) $destination
                }
            }
            # Every runtime copy has verified hashes before any original is moved.
            foreach ($job in $jobs) {
                Assert-Manifests $job.hashes (File-Manifest $job.source) $job.source
                $destination = Join-Path $backup $job.relative
                $expected = [IO.Path]::GetFullPath($destination)
                if (-not $expected.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Unexpected backup move target: $expected"
                }
                Assert-OrdinaryPath $job.source -Tree
                New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
                Move-Item -LiteralPath $job.source -Destination $destination
                $moved.Add($job)
                Assert-Manifests $job.hashes (File-Manifest $destination) $destination
            }
            @{ sourceRepository=$repo; runtimeDirectory=$runtime; jobs=@($jobs.ToArray()); verified=$true } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'migration.json') -Encoding utf8
            Write-Output "Runtime files copied and verified; original files preserved in $backup"
        } catch {
            for ($i = $moved.Count - 1; $i -ge 0; $i--) {
                $job = $moved[$i]
                $original = $job.source
                if (-not (Test-Path -LiteralPath $original)) {
                    Move-Item -LiteralPath (Join-Path $backup $job.relative) -Destination $original
                }
            }
            throw
        }
    } else {
        if ($legacyData.Count) {
            throw 'Existing source data needs migration. Close local servers, then run scripts/dev/setup-runtime.ps1 -Migrate.'
        }
        if (Test-Path -LiteralPath (Join-Path $runtime 'data')) {
            throw 'Unclaimed runtime data already exists; setup will not adopt or overwrite it automatically.'
        }
    }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    if (-not (Test-Path -LiteralPath $marker)) {
        @{ version=1; sourceRepository=$repo; persistentData='data'; createdUtc=[DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding utf8
    }
}
foreach ($relative in 'data', 'logs', 'Tools\re\out\ot') {
    Assert-OrdinaryPath (Join-Path $runtime $relative)
    New-Item -ItemType Directory -Force -Path (Join-Path $runtime $relative) | Out-Null
}
if ($Dependencies) {
    $packages = Join-Path $runtime 'Tools\ooz'
    Assert-OrdinaryPath $packages -Tree
    New-Item -ItemType Directory -Force -Path $packages | Out-Null
    if ($runtime -ne $repo) {
        Copy-Item -LiteralPath (Join-Path $repo 'Tools\ooz\package.json'),
            (Join-Path $repo 'Tools\ooz\package-lock.json') -Destination $packages -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $packages 'node_modules\ooz-wasm'))) {
        & npm.cmd ci --prefix $packages --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "Cannot initialize extraction dependencies in $packages" }
    }
}
if ($PythonTools) {
    $environment = Join-Path $runtime '.venv'
    Assert-OrdinaryPath $environment -Tree
    $python = Join-Path $environment 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) {
        $basePython = (Get-Command python -ErrorAction Stop).Source
        & $basePython -m venv $environment
        if ($LASTEXITCODE -ne 0) { throw "Cannot create Python environment in $environment" }
    }
    $requirements = Join-Path $PSScriptRoot 'requirements-control.txt'
    $version = ((Get-Content -LiteralPath $requirements | Where-Object { $_ -like 'frida==*' }) -split '==')[1]
    & $python -B -c 'import importlib,importlib.util,sys; spec=importlib.util.find_spec(sys.argv[1]); sys.exit(0 if spec and importlib.import_module(sys.argv[1]).__version__ == sys.argv[2] else 1)' frida $version
    if ($LASTEXITCODE -ne 0) {
        & $python -m pip --disable-pip-version-check install --no-input --cache-dir (Join-Path $runtime '.cache\pip') `
            -r $requirements
        if ($LASTEXITCODE -ne 0) { throw "Cannot initialize client-control dependencies in $environment" }
    }
}
