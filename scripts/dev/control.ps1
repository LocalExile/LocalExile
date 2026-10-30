# Short entry point for the native client bridge. Passwords are prompted privately by Python.
param([string]$Command = 'game:status', [string]$Text, [switch]$Stdio, [switch]$PasswordStdin)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'client-path.ps1')
$setupOutput = & (Join-Path $PSScriptRoot 'setup-runtime.ps1') -PythonTools
foreach ($line in $setupOutput) { [Console]::Error.WriteLine($line) }
$python = Join-Path (Get-RuntimeDirectory $repo) '.venv\Scripts\python.exe'
$arguments = @('-B', (Join-Path $PSScriptRoot 'client_control.py'), $Command)
if ($PSBoundParameters.ContainsKey('Text')) { $arguments += $Text }
if ($Stdio) { $arguments += '--stdio' }
if ($PasswordStdin) { $arguments += '--password-stdin' }
Push-Location $repo
try { & $python @arguments; exit $LASTEXITCODE } finally { Pop-Location }
