@echo off
rem Double-click to open LocalExile Filter Fixer, or drag a .filter file onto this file.
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0LocalExileFilterFixer.ps1" -FilterPath "%~1"
if errorlevel 1 (
  echo.
  echo Something went wrong starting the Filter Fixer. See the message above.
  pause
)
