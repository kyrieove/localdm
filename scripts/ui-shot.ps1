# 给已在运行的 LocalDm 窗口截图；可先按名字点一个按钮（UIA Invoke）。
#   pwsh -File scripts/ui-shot.ps1 -Out artifacts\ui\x.png
#   pwsh -File scripts/ui-shot.ps1 -Click "继续这一局" -Out artifacts\ui\x.png
# 截图用 PrintWindow（窗口自己画，不受遮挡影响），保存整幅窗口含边框。
param(
    [string]$Click,
    [Parameter(Mandatory)][string]$Out,
    [string]$Process = 'LocalDm',
    [int]$WaitMs = 1000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShotWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@

$proc = Get-Process -Name $Process -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $proc) { throw "没有找到正在运行且有窗口的进程：$Process" }
$h = $proc.MainWindowHandle

if ($Click) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Click
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { throw "找不到按钮：$Click" }
    $pattern = $null
    if (-not $el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw "按钮没有 Invoke 模式：$Click" }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    Start-Sleep -Milliseconds $WaitMs
}

$r = New-Object ShotWin+RECT
[void][ShotWin]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { throw "窗口尺寸无效（最小化了？）" }
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [ShotWin]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
if (-not $ok) { throw "PrintWindow 失败" }
New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Out))) | Out-Null
$bmp.Save([IO.Path]::GetFullPath($Out), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved: $Out ($w x $ht)"
