# The client tables as SQL in sql/ (Agents/docs/dev/databases.md, "client.db"): a maintainer tool, the only writer of
# sql/base/client/ and sql/updates/client/.
#   .\scripts\dev\client-sql.ps1 diff   after a client patch and extract-data.ps1: one dated update per changed table,
#                                       with only the differing rows; lists the fixes that touch a changed table
#   .\scripts\dev\client-sql.ps1 full   the first import (refuses when sql/base/client/ has tables)
# Reads data\tables\*.json (extract-data.ps1). Never writes data\ or the client.
param([Parameter(Mandatory)][ValidateSet('full', 'diff')][string]$Mode)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$venv = Join-Path $repo '.venv\Scripts\python.exe'
$python = if (Test-Path $venv) { $venv } else { (Get-Command python -ErrorAction SilentlyContinue).Source }
if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
if (-not $python) { throw 'Python not found (python or py on PATH, or .venv)' }
& $python (Join-Path $PSScriptRoot 'client_sql.py') $Mode
if ($LASTEXITCODE -ne 0) { throw "client_sql.py $Mode failed ($LASTEXITCODE)" }
