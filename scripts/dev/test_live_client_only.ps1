# Exercise the actual launch branch with mocks: no client, server, or GGPK is touched.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'live.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('Need', 'Select-LocalGateway')) {
    $definition = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if (-not $definition) { throw "missing function $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$branch = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq '$ClientOnly'
}, $false)
if (-not $branch) { throw 'missing ClientOnly branch' }
$launch = [scriptblock]::Create($branch.Extent.Text)
$repo = 'C:\LocalExile-test'
$ClientOnly = $true
$NoClient = $false
$Only = @()
$Walkmaps = $false
$script:events = [System.Collections.Generic.List[string]]::new()
$script:existing = @()
$script:missingPort = 0
$script:gatewayExit = 0
$script:gatewayArgs = @()
function Get-Process { $script:existing }
function Get-NetTCPConnection {
    param($LocalPort, $State, $ErrorAction)
    if ($LocalPort -ne $script:missingPort) { [pscustomobject]@{ LocalPort = $LocalPort } }
}
function dotnet {
    $script:gatewayArgs = @($args)
    $script:events.Add('gateway')
    $global:LASTEXITCODE = $script:gatewayExit
}
function Start-Process {
    $script:events.Add('launch')
    [pscustomobject]@{ Id = 123 }
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Expect-Failure($pattern) {
    $message = $null
    try { & $launch | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message -like "*$pattern*") "expected '$pattern', got '$message'"
    Assert ($script:events -notcontains 'launch') 'failed preflight launched a client'
}

& $launch | Out-Null
Assert (($script:events -join ',') -eq 'gateway,launch') 'gateway must reset before a closed client launches'
Assert ($script:gatewayArgs[0] -eq (Join-Path $repo 'src\GameServer.Patch\bin\Debug\net10.0\GameServer.Patch.dll')) 'wrong patch executable'
Assert (($script:gatewayArgs[1..4] -join '|') -eq ('gateway|' + (Join-Path $repo 'Client\Content.ggpk') + '|--login|127.0.0.1:20493')) 'gateway must target this checkout and local Auth'

$script:events.Clear()
$script:existing = @([pscustomobject]@{ Id = 42; Path = (Join-Path $repo 'Client\Game.patched.exe') })
& $launch | Out-Null
Assert ($script:events.Count -eq 0) 'existing client must return without writes or launch'
$script:existing = @([pscustomobject]@{ Id = 42; Path = 'C:\Other\Game.patched.exe' })
Expect-Failure 'another patched client is running'
Assert ($script:events.Count -eq 0) 'foreign client must return before gateway write'

$script:existing = @()
$script:missingPort = 20493
Expect-Failure 'missing port 20493'
Assert ($script:events.Count -eq 0) 'missing server must return before gateway write'
$script:missingPort = 0
$script:gatewayExit = 1
Expect-Failure 'could not select local login server'
Assert (($script:events -join ',') -eq 'gateway') 'failed gateway reset must prevent launch'

$script:events.Clear()
$NoClient = $true
Expect-Failure '-ClientOnly cannot be combined'
Assert ($script:events.Count -eq 0) 'invalid options must return before gateway write'
Write-Output 'ClientOnly launch checks passed (mocked; no client or GGPK touched).'
