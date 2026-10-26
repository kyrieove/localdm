param([Parameter(Mandatory)][string]$Title)
# 启动（必要时重新发布）本局主持，剧本库里选指定标题并翻开。只打印结论，不枚举剧本正文控件。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repo = Split-Path $PSScriptRoot -Parent
& pwsh -NoProfile -File "$repo\scripts\start-local-test.ps1" -NoOpen | Out-Null
$exe = Join-Path $repo 'artifacts/publish/win-x64/LocalDm.exe'
$process = $null
for ($i = 0; $i -lt 120; $i++) {
    $process = Get-Process LocalDm -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Select-Object -First 1
    if ($process -and $process.MainWindowHandle -ne 0) { break }
    Start-Sleep -Milliseconds 500
}
if (-not $process -or $process.MainWindowHandle -eq 0) { throw '没有出现主窗口。' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Find-Id([string]$id) {
    $window.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Invoke-Control($element) {
    if ($null -eq $element) { throw '没有找到预期控件。' }
    ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 400
}
Invoke-Control (Find-Id 'NavLibrary')
$list = Find-Id 'PackageList'
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
$target = $null
foreach ($item in $items) {
    $hit = $item.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Title))
    if ($hit) { $target = $item; break }
}
if (-not $target) { throw "剧本库里没有《$Title》（共 $($items.Count) 本）。" }
([System.Windows.Automation.SelectionItemPattern]$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Start-Sleep -Milliseconds 400
Invoke-Control (Find-Id 'OpenButton')
Write-Host "已翻开《$Title》，进程 $($process.Id)"
