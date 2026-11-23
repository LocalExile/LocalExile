# One AI test run against a live client: servers, client and every bridge call stay in this process tree,
# because a tool call that exits takes its whole tree (client and servers included) with it.
# The caller sets PATH (portable toolchain) before starting this. UTF-8 with BOM: the PowerShell 5.1 host reads it.
param(
    [string]$Character = '',
    [string]$Out = ''
)

$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'ai-test-run.log' }
$bridge = Join-Path $repo 'scripts\dev\control.ps1'

function Say([string]$text) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $text
    Add-Content -Path $Out -Value $line -Encoding UTF8
    Write-Host $line
}

# One bridge call; returns the JSON object as text (the last line that is a JSON object).
function Control([string[]]$controlArgs) {
    $raw = & powershell -NoProfile -ExecutionPolicy Bypass -File $bridge @controlArgs 2>&1 | Out-String
    $json = ($raw -split "`r?`n" | Where-Object { $_.Trim().StartsWith('{') } | Select-Object -Last 1)
    if (-not $json) { $json = ($raw -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1) }
    Say ("  <- " + $controlArgs[0] + " :: " + $json)
    $json
}

Remove-Item $Out -ErrorAction SilentlyContinue
Say '=== AI live test run ==='

Say '1. servers + client (live.ps1)'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\dev\live.ps1') -NoBuild -NoExtract 2>&1 |
    ForEach-Object { Say "  live.ps1: $_" }

Say '2. wait for the client'
$up = $false
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Seconds 5
    $status = Control @('game:status')
    if ($status -match '"running": true') { $up = $true; break }
}
if (-not $up) { Say 'client did not come up'; exit 1 }

Say '3. character list'
$list = Control @('character:list')

Say '4. pick a character'
$name = $Character
if (-not $name) {
    $pick = [regex]::Matches($list, '"name":\s*"([^"]+)",\s*"league":[^}]*?"level":\s*(\d+)')
    $best = $pick | Sort-Object { [int]$_.Groups[2].Value } -Descending | Select-Object -First 1
    if ($best) { $name = $best.Groups[1].Value }
}
Say "  chosen: $name"
if (-not $name) { Say 'no character to play'; exit 1 }

Say '5. enter the game'
Control @("character:play:$name")

Say '6. where am I'
Control @('chat', '/pos')

Say '7. monsters here'
Control @('chat', '/mobs')

Say '8. spawn a monster beside me and see it arrive'
Control @('chat', '/spawn_mob Rhoa')
Start-Sleep -Seconds 2
Control @('chat', '/mobs')

Say '9. read game frames (the client is rendering and simulating)'
Control @('game:frames')

Say '10. hold the client for 20 minutes so the caller can keep driving it'
Start-Sleep -Seconds 1200
Say 'exiting (the client goes with this process)'
