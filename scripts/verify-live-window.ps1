# 真实 WPF 窗口验收：启动本机 exe，用 UI Automation 点按钮，把窗口本身截成 PNG。
#
# 本机有两块屏：主屏 1920x1080（100%），副屏 1920x1080 物理 @125%。
# 因此「常规」与「缩放」两种截图都是真实 DPI，不需要改任何系统设置：
#   artifacts/ui/desktop-100-*.png  —— 窗口放在主屏（100%）
#   artifacts/ui/desktop-125-*.png  —— 同一窗口移到 125% 的副屏
#
# - 运行数据根目录指向临时目录（LOCALDM_DATA_ROOT），不碰真实存档与设置。
# - 只保存窗口矩形那一块；不落盘整屏。
# - 不修改系统任何设置。
param(
    [string]$ExePath = "D:\2\dev\dm\src\LocalDm.App\bin\Debug\net10.0-windows\LocalDm.exe",
    [string]$OutDir = "D:\2\dev\dm\artifacts\ui"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LiveWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ("localdm-live-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$env:LOCALDM_DATA_ROOT = $dataRoot

$primary = [System.Windows.Forms.Screen]::PrimaryScreen
$secondary = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1

Write-Host "data root: $dataRoot"
Write-Host "primary: $($primary.Bounds)  secondary: $($secondary.Bounds)"


$proc = Start-Process -FilePath $ExePath -PassThru
$window = $null

function Get-Buttons {
    $all = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $out = @()
    foreach ($e in $all) {
        if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { $out += $e }
    }
    return $out
}

function Find-Button([string]$name, [int]$timeoutMs = 8000) {
    $until = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $until) {
        foreach ($e in (Get-Buttons)) { if ($e.Current.Name -eq $name) { return $e } }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Invoke-Button([string]$name) {
    $el = Find-Button $name
    if ($null -eq $el) { throw "找不到按钮：$name（界面没有进入预期状态）" }
    $pattern = $null
    if (-not $el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "按钮没有 Invoke 模式：$name"
    }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    Write-Host "  点击：$name"
    Start-Sleep -Milliseconds 900
}

function Find-ByAutomationId([string]$id) {
    $all = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { if ($e.Current.AutomationId -eq $id) { return $e } }
    return $null
}

function Set-Text([string]$id, [string]$value) {
    $el = Find-ByAutomationId $id
    if ($null -eq $el) { throw "找不到输入框：$id" }
    $pattern = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.ValuePattern]$pattern).SetValue($value)
        Write-Host "  输入 $id = $value"
    }
    else { throw "输入框没有 Value 模式：$id" }
    Start-Sleep -Milliseconds 400
}

function Show-Buttons {
    foreach ($e in (Get-Buttons)) {
        $c = $e.Current
        Write-Host ("    - {0} at y={1} offscreen={2}" -f $c.Name, [int]$c.BoundingRectangle.Y, $c.IsOffscreen)
    }
}

# 只挪位置、不改尺寸：窗口大小由应用自己按 DIP 决定，
# 高 DPI 屏上自然会得到更多物理像素 —— 那正是要看的缩放效果。
function Move-Window([int]$x, [int]$y) {
    # SWP_NOSIZE(1) | SWP_NOZORDER(4) | SWP_NOACTIVATE(16)
    [LiveWin]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $x, $y, 0, 0, 21) | Out-Null
    Start-Sleep -Milliseconds 1500
}

# 截图不依赖谁拥有前台：PrintWindow 让窗口自己把画面画进 DC，
# 即使被别的程序盖住也能拿到本窗口的内容（实测屏幕拷贝曾经拍到过别人家的窗口）。
function Show-Window {
    $h = $proc.MainWindowHandle
    [void][LiveWin]::ShowWindow($h, 9)      # SW_RESTORE
    [void][LiveWin]::BringWindowToTop($h)
    [void][LiveWin]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
}
function Save-Window([string]$fileName) {
    Show-Window

    # 用 PrintWindow 让窗口把整幅画面（含边框）画进一张窗口大小的位图，
    # 再按边框厚度裁出客户区。这样不依赖 z 序，也不会把桌面上的别的东西带进来。
    $frame = New-Object LiveWin+RECT
    [LiveWin]::GetWindowRect($proc.MainWindowHandle, [ref]$frame) | Out-Null
    $ww = $frame.Right - $frame.Left
    $wh = $frame.Bottom - $frame.Top
    if ($ww -le 0 -or $wh -le 0) { throw "窗口尺寸无效" }

    $client = New-Object LiveWin+RECT
    [LiveWin]::GetClientRect($proc.MainWindowHandle, [ref]$client) | Out-Null
    $cw = $client.Right - $client.Left
    $ch = $client.Bottom - $client.Top
    if ($cw -le 0 -or $ch -le 0) { throw "客户区尺寸无效" }

    $full = New-Object System.Drawing.Bitmap $ww, $wh
    $gfx = [System.Drawing.Graphics]::FromImage($full)
    $hdc = $gfx.GetHdc()
    $painted = [LiveWin]::PrintWindow($proc.MainWindowHandle, $hdc, 2)   # PW_RENDERFULLCONTENT
    $gfx.ReleaseHdc($hdc)
    $gfx.Dispose()
    if (-not $painted) { throw "PrintWindow 失败，无法确认画面来源。" }

    $border = [int][Math]::Floor(($ww - $cw) / 2)
    $title = [int][Math]::Floor($wh - $ch - 2 * $border)
    $leftInset = 1
    $topInset = 1
    $rightInset = 1
    $bottomInset = 1
    $x = $border + $leftInset
    $y = $title + $border + $topInset
    $w = $cw - $leftInset - $rightInset
    $h = $ch - $topInset - $bottomInset
    if ($x -lt 0 -or $y -lt 0 -or $x + $w -gt $ww -or $y + $h -gt $wh) {
        throw "裁剪区域超出位图（window=${ww}x${wh} client=${cw}x${ch}）"
    }

    Write-Host "    window=${ww}x${wh} client=${cw}x${ch} border=$border title=$title"

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.DrawImage(
        $full,
        (New-Object System.Drawing.Rectangle 0, 0, $w, $h),
        (New-Object System.Drawing.Rectangle $x, $y, $w, $h),
        [System.Drawing.GraphicsUnit]::Pixel)
    $g2.Dispose()
    $full.Dispose()

    $path = Join-Path $OutDir $fileName
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $info = Get-Item $path
    Write-Host "  截图：$path (${w}x${h}, $($info.Length) bytes)"
}

try {
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and $proc.MainWindowHandle -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 200; $proc.Refresh()
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) { throw "窗口没有出现" }
    Start-Sleep -Milliseconds 1500
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    Write-Host "window: $($window.Current.Name)"

    # ── 100% 主屏 ──────────────────────────────────────────────
    Move-Window ($primary.Bounds.X + 30) ($primary.Bounds.Y + 30)
    Save-Window "desktop-100-library.png"

    Invoke-Button "体验演示" | Out-Null
    Start-Sleep -Milliseconds 1200
    Save-Window "desktop-100-prepared.png"

    Write-Host "  主持台按钮："
    Show-Buttons

    Invoke-Button "开始这一局" | Out-Null
    Save-Window "desktop-100-playing.png"

    Invoke-Button "进入搜证" | Out-Null
    Save-Window "desktop-100-search.png"

    # 逐个发牌：按钮文字应随待发编号变化，发完才允许推进
    Write-Host "  搜证环节按钮："
    Show-Buttons
    Invoke-Button "发放材料 线索包一"
    Invoke-Button "发放材料 线索包二"
    Save-Window "desktop-100-search-released.png"
    Write-Host "  发完材料后的按钮（应出现「进入讨论」且可用）："
    Show-Buttons

    # ── 设置页：声音与模型都收在这里；游玩界面不再有 caller key 与文档工具路径 ──
    Invoke-Button "偏好设置" | Out-Null
    Start-Sleep -Milliseconds 800
    Save-Window "desktop-100-settings.png"
    Invoke-Button "回到本局" | Out-Null

    # ── 投票与揭秘：票数校验照旧机器把关，按钮点一下就是确认 ──
    Invoke-Button "进入讨论"
    Invoke-Button "进入投票"
    Set-Text "VoteBox" "A=3, B=2"
    Invoke-Button "确认提交汇总"
    Invoke-Button "进入真相"
    Save-Window "desktop-100-reveal-ready.png"
    Invoke-Button "公开真相"
    Save-Window "desktop-100-revealed.png"

    # ── 125% 副屏：同一窗口、同样的 DIP 内容，更高的缩放 ────────
    if ($null -ne $secondary) {
        Move-Window ($secondary.Bounds.X + 30) ($secondary.Bounds.Y + 30)
        Save-Window "desktop-125-revealed.png"
        Write-Host "  125% 下的窗口信息："
        $r = New-Object LiveWin+RECT
        [LiveWin]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
        Write-Host "    window rect: $($r.Right - $r.Left)x$($r.Bottom - $r.Top) at ($($r.Left),$($r.Top))"
        Show-Buttons
    }

    Write-Host "完成"
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 800
        if (-not $proc.HasExited) { $proc.Kill() }
    }
    Remove-Item Env:LOCALDM_DATA_ROOT -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    try { Remove-Item -Path $dataRoot -Recurse -Force -ErrorAction Stop } catch { Write-Host "临时目录残留：$dataRoot" }
}
