# Real filesystem migration in a temporary checkout, with fake data and mocked process/git inventories.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('runtime-setup-' + [guid]::NewGuid().ToString('N'))
$savedRuntime = $env:Game_RUNTIME_DIR
$env:Game_RUNTIME_DIR = $null
function Assert($value, $message) { if (-not $value) { throw $message } }
function Get-CimInstance { if ($global:runtimeSetupTestRunning) { [pscustomobject]@{ Name='dotnet.exe'; CommandLine='dotnet GameServer.Auth.dll' } } }
function git { $global:LASTEXITCODE=0; 'src/Fake/Fake.csproj' }
function Expect-Failure($script, $pattern) {
    $message = $null
    try { & $script | Out-Null } catch { $message=$_.Exception.Message }
    Assert ($message -like "*$pattern*") "expected '$pattern', got '$message'"
}
try {
    $repo = Join-Path $fixture 'source with spaces'
    $runtime = Join-Path $fixture 'runtime with spaces'
    foreach ($relative in 'scripts\dev', 'config', 'data\schema', 'data\tables', 'logs', 'Tools\re\out\ot',
        'Tools\ooz\node_modules\ooz-wasm', 'src\Fake\bin', 'src\Fake\obj') {
        New-Item -ItemType Directory -Force -Path (Join-Path $repo $relative) | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'setup-runtime.ps1'), (Join-Path $PSScriptRoot 'client-path.ps1') `
        -Destination (Join-Path $repo 'scripts\dev')
    $files = @('data\schema\schema.min.json', 'data\login-keys.json', 'data\saves.db', 'data\saves.db-wal',
        'data\tables\WorldAreas.json', 'logs\live-auth.log', 'Tools\re\out\ot\cached.ot',
        'Tools\ooz\node_modules\ooz-wasm\index.js', 'src\Fake\bin\Fake.dll', 'src\Fake\obj\project.assets.json')
    foreach ($file in $files) { Set-Content -LiteralPath (Join-Path $repo $file) -Value "fixture $file" }
    @{runtimeDirectory='../runtime with spaces'} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $repo 'config\client.local.json') -Encoding utf8
    $setup = Join-Path $repo 'scripts\dev\setup-runtime.ps1'
    Expect-Failure { & $setup } 'Existing source data needs migration'
    $global:runtimeSetupTestRunning=$true
    Expect-Failure { & $setup -Migrate } 'stop local GameServer processes'
    $global:runtimeSetupTestRunning=$false
    # Destination collision must leave both copies intact.
    New-Item -ItemType Directory -Force -Path (Join-Path $runtime 'data') | Out-Null
    $collision = Join-Path $runtime 'data\login-keys.json'
    Set-Content -LiteralPath $collision -Value 'unrelated destination'
    Expect-Failure { & $setup -Migrate } 'refuses to overwrite'
    Assert ((Get-Content -LiteralPath $collision) -eq 'unrelated destination') 'overwrote existing destination'
    Assert (Test-Path -LiteralPath (Join-Path $repo 'data\saves.db-wal')) 'touched source before collision check'
    Assert ($collision.StartsWith($fixture + '\')) 'unexpected collision fixture path'
    Remove-Item -LiteralPath $collision
    & $setup -Migrate
    $backup = @(Get-ChildItem -LiteralPath $fixture -Directory -Filter 'runtime-migration-backup-*')
    Assert ($backup.Count -eq 1) 'missing migration backup'
    $receipt = Get-Content -LiteralPath (Join-Path $backup[0].FullName 'migration.json') -Raw | ConvertFrom-Json
    Assert $receipt.verified 'migration did not verify hashes'
    foreach ($file in $files) {
        if ($file -like 'data\schema\*') { Assert (Test-Path -LiteralPath (Join-Path $repo $file)) 'moved tracked schema'; continue }
        $original = Join-Path $backup[0].FullName $file
        Assert (Test-Path -LiteralPath $original) "missing original backup: $file"
        Assert (-not (Test-Path -LiteralPath (Join-Path $repo $file))) "runtime file left in source: $file"
        if ($file -notlike 'src\*') {
            Assert ((Get-FileHash -LiteralPath $original).Hash -eq
                (Get-FileHash -LiteralPath (Join-Path $runtime $file)).Hash) "runtime hash mismatch: $file"
        }
    }
    & $setup
    Expect-Failure { & $setup -Migrate } 'already initialized'
    Set-Content -LiteralPath (Join-Path $repo 'data\saves.db') -Value 'accidental second database'
    Expect-Failure { & $setup } 'reappeared in the source'
    # Ordinary directories are required even if a junction points to another harmless fixture directory.
    $plain = Join-Path $fixture 'plain runtime'
    $junction = Join-Path $fixture 'linked runtime'
    New-Item -ItemType Directory -Path $plain | Out-Null
    New-Item -ItemType Junction -Path $junction -Target $plain | Out-Null
    try {
        @{runtimeDirectory=$junction} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $repo 'config\client.local.json') -Encoding utf8
        Expect-Failure { & $setup } 'ordinary directories'
    } finally {
        Assert ([IO.Path]::GetFullPath($junction).StartsWith($fixture + '\')) 'unexpected junction cleanup path'
        Assert (((Get-Item -LiteralPath $junction).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'fixture junction changed'
        Remove-Item -LiteralPath $junction -Force
    }
    Write-Output 'Runtime migration, verified backup, collision/process refusal, idempotence and split-data guard passed.'
} finally {
    $env:Game_RUNTIME_DIR=$savedRuntime
    Remove-Variable -Name runtimeSetupTestRunning -Scope Global -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^runtime-setup-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
