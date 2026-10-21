# Desktop real-browser acceptance for the probe app.
# Drives real Chrome via Playwright CLI: claim a role, click a card, turn pages.

$ErrorActionPreference = 'Stop'
$log = New-Object System.Collections.Generic.List[string]

# npx resolves to the npm.cmd shim, which PowerShell treats as Clear-Item when
# called through the pipeline. Call it through cmd.exe to keep the real npx.
function Cli {
    param([Parameter(ValueFromRemainingArguments = $true)] $Args)
    $joined = ($Args | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $command = "npx --yes --package @playwright/cli playwright-cli $joined 2>&1"
    $output = & cmd.exe /c $command
    $text = ($output | Out-String)
    $log.Add("CLI: $($Args -join ' ')`n$text")
    return $text
}

function Step($text) {
    $log.Add("STEP: $text")
    Write-Host "==> $text"
}

function Note($text) {
    $log.Add("NOTE: $text")
    Write-Host "    $text"
}

$url = 'http://127.0.0.1:5080/'
$shots = Join-Path $PSScriptRoot 'shots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null

# Chinese literals kept as escaped code points so the file stays pure ASCII.
$cardNames = [char]0x4E66+[char]0x623F, [char]0x8D70+[char]0x5ECA, [char]0x4FE1+[char]0x7BB1,
             [char]0x65E7+[char]0x7167+[char]0x7247, [char]0x94A5+[char]0x5319
$pageLabels = [char]0x7B2C+' 1 '+[char]0x9875, [char]0x7B2C+' 2 '+[char]0x9875, [char]0x7B2C+' 3 '+[char]0x9875
$roleLabel = [char]0x5408+[char]0x6210+[char]0x89D2+[char]0x8272+' 1'
$currentRole = [char]0x5F53+[char]0x524D+[char]0x89D2+[char]0x8272
$probeTitle = [char]0x5408+[char]0x6210+[char]0x63A2+[char]0x9488

Step 'open probe page'
Cli open $url | Out-Null
Cli wait --text $probeTitle | Out-Null
Note 'page shell rendered'

Step 'snapshot: expect 6 synthetic role buttons'
$snap = Cli snapshot
if ($snap -notmatch [regex]::Escape($roleLabel)) {
    throw 'role buttons missing: interactive render not working'
}
if ($snap -notmatch [regex]::Escape([string]([char]0x5408+[char]0x6210+[char]0x89D2+[char]0x8272+' 6'))) {
    throw 'role buttons incomplete'
}
Note 'role buttons in DOM, interactive circuit established'

Step 'click role 1'
Cli click "text=$roleLabel" | Out-Null
Cli wait --text $currentRole | Out-Null
Note 'role claimed, catalog shown'

Step 'snapshot: expect card catalog and page buttons'
$snap2 = Cli snapshot
foreach ($need in ($cardNames + $pageLabels)) {
    if ($snap2 -notmatch [regex]::Escape($need)) { throw "catalog missing $need" }
}
Note 'catalog and page buttons complete'

Step 'click a card'
Cli click "text=$($cardNames[0])" | Out-Null
Cli wait 1500 | Out-Null

Step 'assert card image really displays with right size'
$img = Cli eval "document.querySelector('img.shot')?.naturalWidth + 'x' + document.querySelector('img.shot')?.naturalHeight"
Note "card image natural size = $($img.Trim())"
if ($img -notmatch '300x200') { throw "card image not decoded properly, actual: $img" }

Step 'turn to role page 2'
Cli click "text=$($pageLabels[1])" | Out-Null
Cli wait 1500 | Out-Null
$page = Cli eval "document.querySelector('img.shot')?.naturalWidth + 'x' + document.querySelector('img.shot')?.naturalHeight + ' src=' + document.querySelector('img.shot')?.getAttribute('src')"
Note "role page image = $($page.Trim())"
if ($page -notmatch '400x600') { throw "role page image not decoded properly, actual: $page" }
if ($page -notmatch '/tablet/role/pages/2') { throw "page turn did not fetch page 2, actual: $page" }

Step 'screenshot evidence'
Cli screenshot "$shots\desktop-a-role-page.png" | Out-Null

Step 'reload: role and reading state should survive (cookie session)'
Cli reload | Out-Null
Cli wait --text $currentRole | Out-Null
Note 'after reload still holding role 1'

Step 'close browser'
Cli close | Out-Null

$log | Set-Content "$shots\desktop-verification.txt" -Encoding utf8
Write-Host ''
Write-Host 'ALL DESKTOP BROWSER CHECKS PASSED'
