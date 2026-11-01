# Builds LocalExileGUI.exe with the C# compiler that ships with Windows (.NET Framework 4.x); nothing to install.
# The exe belongs beside LocalExile.exe, whose icon it takes when it is there.
#   -Out <path of LocalExileGUI.exe>   required
param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object FullName
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

$iconArgs = @()
$launcher = Join-Path (Split-Path $Out) 'LocalExile.exe'
if (Test-Path $launcher) {
    Add-Type -AssemblyName System.Drawing
    $icon = Join-Path ([System.IO.Path]::GetTempPath()) 'LocalExileGUI.ico'
    $stream = [System.IO.File]::Create($icon)
    [System.Drawing.Icon]::ExtractAssociatedIcon($launcher).Save($stream)
    $stream.Close()
    $iconArgs = @("/win32icon:$icon")
}

& $csc /nologo /target:winexe /optimize+ /debug- /codepage:65001 "/out:$Out" @iconArgs `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    @sources
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host "built $((Resolve-Path $Out).Path)"
