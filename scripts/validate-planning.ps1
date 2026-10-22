[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$feature = Join-Path $root 'specs/001-local-dm-mvp'
$required = @('spec.md','plan.md','research.md','data-model.md','quickstart.md','tasks.md','contracts/desktop-interactions.md','contracts/integrations.md','contracts/game-commands.md','contracts/document-package.md','contracts/tablet-distribution.md')
foreach ($file in $required) {
    $path = Join-Path $feature $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing artifact: $file" }
    $content = Get-Content -Raw -Encoding utf8 -LiteralPath $path
    if ($content -match '\[NEEDS CLARIFICATION|\[FEATURE\]|\[###|\bTODO\b|\bTBD\b') { throw "Unresolved placeholder: $file" }
}
$taskText = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $feature 'tasks.md')
$taskLines = @($taskText -split '\r?\n' | Where-Object { $_ -match '^- \[' })
$storyCounts = @{}
$ids = @()
$story = $null
foreach ($line in ($taskText -split '\r?\n')) {
    if ($line -match '^## Phase ') {
        $story = if ($line -match 'User Story (\d+)') { 'US' + $Matches[1] } else { $null }
    }
    if ($line -notmatch '^- \[') { continue }
    if ($line -notmatch '^- \[ \] T(?<id>\d{3}) (?<parallel>\[P\] )?(?<label>\[US\d+\] )?(?<description>.+)$') { throw "Invalid task format: $line" }
    $ids += [int]$Matches.id
    $label = ([string]$Matches.label).Trim().Trim('[',']')
    $description = $Matches.description
    if ($label -ne $story -and -not ([string]::IsNullOrEmpty($label) -and $null -eq $story)) { throw "Wrong story label: $line" }
    if ($description -notmatch 'src/|tests/|docs/|fixtures/|packaging/|\.gitignore|LocalDm\.sln') { throw "Task lacks concrete path: $line" }
    if ($label) { $storyCounts[$label] = 1 + [int]$storyCounts[$label] }
}
for ($i=0; $i -lt $ids.Count; $i++) { if ($ids[$i] -ne $i+1) { throw "Nonsequential or duplicate task ID at $i" } }
if ($ids.Count -ne $taskLines.Count -or $ids.Count -ne 84) { throw "Unexpected task count: $($ids.Count)" }
foreach ($label in @('US1','US2','US3','US4','US5','US6')) { if (-not $storyCounts.ContainsKey($label)) { throw "Missing story: $label" } }
$model = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $feature 'data-model.md')
$constraints = [regex]::Matches($model, '(?m)^- C\d{2}「([^」]+)」')
if ($constraints.Count -ne 17) { throw "Expected 17 shared constraints, found $($constraints.Count)" }
foreach ($constraint in $constraints) { if (-not $taskText.Contains($constraint.Groups[1].Value)) { throw "Unmapped constraint: $($constraint.Value)" } }
$constitution = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root '.specify/memory/constitution.md')
if ($constitution -notmatch '\*\*Version\*\*:\s*(?<ver>\d+\.\d+\.\d+)') { throw 'Constitution version missing' }
$constVersion = $Matches.ver
if ($constVersion -ne '5.0.0') { throw "Constitution version mismatch: expected 5.0.0, found $constVersion" }
if ($constitution -notmatch 'OpenLess' -or $constitution -notmatch 'Windows Magpie') { throw 'Current integrations missing from constitution' }
$state = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root '.specify/feature.json') | ConvertFrom-Json
if ($state.feature_directory.Replace('\','/') -ne 'specs/001-local-dm-mvp') { throw 'Wrong active feature' }
$parallelCount = @($taskLines | Where-Object { $_ -match ' T\d{3} \[P\] ' }).Count
[PSCustomObject]@{status='PASS';artifacts=$required.Count;tasks=$ids.Count;parallel=$parallelCount;stories=$storyCounts;constraints=$constraints.Count;constitution=$constVersion} | ConvertTo-Json -Depth 4
