# Run on the release candidate. -ZipDirectory uses previously downloaded official ZIPs.
param([string]$ZipDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$releases = Get-Content (Join-Path $repo 'scripts/dev/mod-compat/releases.json') -Raw | ConvertFrom-Json
$pins = @($releases | Where-Object id -eq 'gm-commands')
if (!$pins.Count) { throw 'No GM Commands release pinned in scripts/dev/mod-compat/releases.json.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$run = [IO.Directory]::CreateTempSubdirectory('localexile-release-gm-')
try {
    foreach ($pin in $pins) {
        $name = "gm-commands-$($pin.version).zip"
        $case = New-Item -ItemType Directory -Path (Join-Path $run.FullName $pin.version)
        if ($ZipDirectory) {
            $zip = (Resolve-Path -LiteralPath (Join-Path $ZipDirectory $name)).Path
        } else {
            $zip = Join-Path $case $name
            Invoke-WebRequest "https://github.com/LocalExile/LocalExile-Mods/releases/download/gm-commands-v$($pin.version)/$name" -OutFile $zip
        }
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $pin.sha256) { throw "SHA-256 mismatch for $name" }
        $mods = Join-Path $case 'mods'
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $mods 'gm-commands')
        $test = 'GameServer.Gateway.Tests.PublishedGmCommandsTests.Published_mod_sends_position_and_death_screen'
        & dotnet test (Join-Path $repo 'tests/GameServer.Gateway.Tests') -c Release --filter "FullyQualifiedName=$test" `
            --environment "Game_RELEASE_GM_FOLDER=$mods" --environment "Game_CLIENT_DB=$(Join-Path $run.FullName 'client.db')" `
            --environment "Game_SQL_DIR=$(Join-Path $repo 'sql')" --results-directory $case.FullName --logger 'trx;LogFileName=gm.trx'
        if ($LASTEXITCODE -ne 0) { throw "GM Commands $($pin.version) release check failed." }
        [xml]$result = Get-Content (Join-Path $case 'gm.trx') -Raw
        $outcomes = @($result.TestRun.Results.UnitTestResult)
        if ($outcomes.Count -ne 1 -or $outcomes[0].testName -ne $test -or $outcomes[0].outcome -ne 'Passed') {
            throw "The required GM Commands test did not pass for $name."
        }
        Write-Host "PASS GM Commands $($pin.version): /pos returned (123, 87) in ReleaseCheck and /death_screen 02 sent 005502."
    }
} finally {
    $target = Get-Item -LiteralPath $run.FullName
    if ($target.Parent.FullName -ne [IO.Path]::TrimEndingDirectorySeparator($tempRoot) -or
        $target.Name -notlike 'localexile-release-gm-*' -or ($target.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected temporary directory: $target"
    }
    Remove-Item -LiteralPath $target.FullName -Recurse -Force
}
