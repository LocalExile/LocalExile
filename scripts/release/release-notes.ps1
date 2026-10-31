# Writes a release's notes from the commits since the last tag (Agents/docs/dev/releases.md): Conventional Commit subjects
# by type, the others under Other, then the contributors by GitHub login. It prints the bump the commits suggest.
# It only suggests: the release's version is always the one the person starting it typed.
#   -Version <x.y.z[-pre]> -Output <file> [-Head <commit>]     needs an authenticated gh
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Output, [string]$Head = 'HEAD')

$ErrorActionPreference = 'Stop'
$repository = 'LocalExile/LocalExile'
# Neither bots nor agents' own identities are contributors to credit.
$notPeople = 'opencode', 'LocalExile Agent', 'LocalExile', 'Claude', 'github-actions'

$previous = @(git tag --list 'v*' --sort=-v:refname --merged $Head) | Select-Object -First 1
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("For the Path of Exile **3.29.3b** client. Download the zip below, extract it, run ``LocalExile.exe``; nothing else to install.")
$lines.Add('**Always start the game with `LocalExile.exe`:** any other way can update the client, which breaks the setup.')
$lines.Add('')
$lines.Add('## Highlights')
$lines.Add('')
$lines.Add('<!-- The person publishing writes these. -->')
$lines.Add('')

function Add-Contributors($names) {
    $lines.Add('## Contributors')
    $lines.Add('')
    $lines.Add((($names | Sort-Object -Unique) -join ', '))
    $lines.Add('')
}

if (-not $previous) {
    # The first release has no range to list: its whole history is the project's start.
    $lines.Add('The first release.')
    $lines.Add('')
    $people = gh api "repos/$repository/contributors" --paginate --jq '.[] | select(.type == "User") | "@" + .login'
    if ($LASTEXITCODE -ne 0) { throw 'gh api contributors failed' }
    Add-Contributors @($people | Where-Object { $_ })
    Write-Host 'first release: no bump to suggest'
}
else {
    $titles = [ordered]@{ feat = 'Features'; fix = 'Fixes'; perf = 'Performance'; docs = 'Documentation'; refactor = 'Refactoring'; other = 'Other' }
    $groups = @{}
    $bump = 'patch'
    # %x1f and %x1e: neither is in a subject, and a body can break a line.
    $log = (git log --no-merges --format='%h%x1f%s%x1f%b%x1e' "$previous..$Head") -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'git log failed' }
    foreach ($commit in $log -split [char]0x1e) {
        $parts = $commit.Trim() -split [char]0x1f
        if ($parts.Count -lt 2) { continue }
        $sha, $subject, $body = $parts
        $type = 'other'
        if ($subject -match '^(?<type>[a-z]+)(\([^)]+\))?(?<breaking>!)?:\s*(?<text>.+)$') {
            $text = $Matches.text
            $breaking = $Matches.breaking -or "$body" -match '(?m)^BREAKING CHANGE:'
            if ($titles.Contains($Matches.type) -and $Matches.type -ne 'other') { $type = $Matches.type } elseif ($Matches.type -in 'chore', 'ci', 'build', 'test', 'style') { continue }
            if ($breaking) { $bump = 'breaking' } elseif ($type -eq 'feat' -and $bump -eq 'patch') { $bump = 'minor' }
        }
        else { $text = $subject }
        if (-not $groups.ContainsKey($type)) { $groups[$type] = [System.Collections.Generic.List[string]]::new() }
        $groups[$type].Add("- $text ($sha)")
    }
    foreach ($type in $titles.Keys) {
        if (-not $groups.ContainsKey($type)) { continue }
        $lines.Add("## $($titles[$type])")
        $lines.Add('')
        $lines.AddRange([string[]]$groups[$type])
        $lines.Add('')
    }
    # A login when GitHub knows the commit's author; else the name, never the email.
    $sha = (git rev-parse $Head).Trim()
    $people = gh api "repos/$repository/compare/$previous...$sha" --paginate --jq '.commits[] | if (.author.login // "") != "" then (if .author.type == "User" then "@" + .author.login else empty end) else .commit.author.name end'
    if ($LASTEXITCODE -ne 0) { throw 'gh api compare failed' }
    Add-Contributors @($people | Where-Object { $_ -and $_ -notin $notPeople })
    $lines.Add("**Full changes:** https://github.com/$repository/compare/$previous...v$Version")
    Write-Host "commits since $previous suggest a $bump bump (0.x: breaking or feat raise the minor, fix the patch)"
}

$lines.Add('')
$lines.Add('Verify the download against the `.sha256` file, or with `gh attestation verify <zip> --repo LocalExile/LocalExile`. Not signed: Windows SmartScreen and antivirus may warn.')
Set-Content $Output $lines -Encoding utf8
