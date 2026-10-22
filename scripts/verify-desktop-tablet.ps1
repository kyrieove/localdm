param(
    [Parameter(Mandatory)][string]$ExpectedTitle,
    [int]$PackageIndex = 1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot 'artifacts/publish/win-x64/LocalDm.exe'
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
for ($i = 0; $i -lt 180 -and $process.MainWindowHandle -eq 0; $i++) {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) { throw '发布版提前退出。' }
}
if ($process.MainWindowHandle -eq 0) { throw '发布版没有出现主窗口。' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Find-Id([string]$id) {
    return $window.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Invoke-Control($element) {
    if ($null -eq $element) { throw '没有找到预期控件。' }
    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 350
}
function Find-ProcessButton([string]$name) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst($scope, $condition)
}

# 只检查公开音色选项；不枚举或打印剧本正文控件。
Invoke-Control (Find-Id 'NavSetup')
$voices = Find-Id 'VoiceSelector'
([System.Windows.Automation.ExpandCollapsePattern]$voices.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
Start-Sleep -Milliseconds 250
$voiceItems = $voices.FindAll($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
$cloud = $null
foreach ($item in $voiceItems) { if ($item.Current.Name -match '云希|YunxiNeural') { $cloud = $item; break } }
if ($null -eq $cloud) { throw '发布版未能列出微软云希音色。' }
([System.Windows.Automation.SelectionItemPattern]$cloud.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Invoke-Control (Find-ProcessButton '保存设置')
Invoke-Control (Find-Id 'NavLibrary')
$packages = Find-Id 'PackageList'
$items = $packages.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
if ($PackageIndex -ge $items.Count) { throw '剧本库没有指定序号的包。' }
([System.Windows.Automation.SelectionItemPattern]$items[$PackageIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Invoke-Control (Find-Id 'OpenButton')
Start-Sleep -Milliseconds 700
Invoke-Control (Find-Id 'NavSetup')
Invoke-Control (Find-Id 'TabletConnectionToggle')
$copy = $null
for ($i = 0; $i -lt 120; $i++) {
    $copy = Find-ProcessButton '复制网址'
    if ($null -ne $copy -and $copy.Current.IsEnabled) { break }
    Start-Sleep -Milliseconds 250
}
Invoke-Control $copy
$lanUrl = Get-Clipboard
if ($lanUrl -notmatch '^http://[0-9.]+:([0-9]+)/$') { throw '软件未复制有效的本局网址。' }
$localUrl = "http://127.0.0.1:$($Matches[1])/"
$null = Invoke-WebRequest $localUrl -SessionVariable tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10
$catalog = (Invoke-WebRequest ($localUrl + 'tablet/catalog') -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
if ($catalog.storyTitle -ne $ExpectedTitle) { throw '网页没有提供指定的真实剧本。' }
$framework = Invoke-WebRequest ($localUrl + '_framework/blazor.web.js') -NoProxy -UseBasicParsing -TimeoutSec 10
$role = $catalog.roles[0].roleId
$null = Invoke-WebRequest ($localUrl + 'tablet/claim?roleId=' + [uri]::EscapeDataString($role)) -Method Post -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10
$book = (Invoke-WebRequest ($localUrl + 'tablet/role/book') -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
$source = Invoke-WebRequest ($localUrl + 'tablet/role/source/1') -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10
$card = $catalog.cards | Where-Object isAvailable | Select-Object -First 1
$front = Invoke-WebRequest ($localUrl + 'tablet/cards/' + [uri]::EscapeDataString($card.cardId) + '/front') -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10
$feed = (Invoke-WebRequest ($localUrl + 'tablet/public?after=0') -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
if (-not $feed.sessionId) { throw '网页未绑定桌面本局。' }
$null = Invoke-WebRequest ($localUrl + 'tablet/release') -Method Post -WebSession $tabletSession -NoProxy -UseBasicParsing -TimeoutSec 10
# 设置中的同一个按钮必须真正关闭监听，再重新提供当前真实剧本。
Invoke-Control (Find-Id 'TabletConnectionToggle')
$closed = $false
try { $null = Invoke-WebRequest ($localUrl + 'tablet/catalog') -NoProxy -UseBasicParsing -TimeoutSec 2 }
catch { $closed = $true }
if (-not $closed) { throw '关闭平板连接后，旧服务仍可访问。' }
Invoke-Control (Find-Id 'TabletConnectionToggle')
for ($i = 0; $i -lt 120; $i++) {
    $copy = Find-ProcessButton '复制网址'
    if ($null -ne $copy -and $copy.Current.IsEnabled) { break }
    Start-Sleep -Milliseconds 250
}
Invoke-Control $copy
$lanUrl = Get-Clipboard
if ($lanUrl -notmatch '^http://[0-9.]+:([0-9]+)/$') { throw '重新开启后未取得有效网址。' }
$localUrl = "http://127.0.0.1:$($Matches[1])/"
$reopened = (Invoke-WebRequest ($localUrl + 'tablet/catalog') -NoProxy -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
if ($reopened.storyTitle -ne $ExpectedTitle) { throw '重新开启后剧本发生变化。' }
[PSCustomObject]@{
    ProcessId = $process.Id; LocalUrl = $localUrl; LanUrl = $lanUrl
    Roles = $catalog.roles.Count; Cards = $catalog.cards.Count
    Chapters = $book.chapters.Count; SourceBytes = $source.RawContentLength
    CardBytes = $front.RawContentLength; FrameworkStatus = $framework.StatusCode
    CurrentSessionBound = [bool]$feed.sessionId; PublicMessages = $feed.messages.Count
    MicrosoftVoiceListedAndSaved = $true
    SettingsToggleClosedAndReopened = $true
} | ConvertTo-Json
