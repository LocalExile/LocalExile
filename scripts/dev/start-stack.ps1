$env:PATH = 'D:\大肥鱼工作区\node\node-v24.4.1-win-x64;D:\大肥鱼工作区\dotnet-sdk;D:\大肥鱼工作区\python;' + $env:PATH
Set-Location 'D:\大肥鱼工作区\LocalExile'
& powershell -NoProfile -ExecutionPolicy Bypass -File 'D:\大肥鱼工作区\LocalExile\scripts\dev\live.ps1' -NoBuild -NoExtract
