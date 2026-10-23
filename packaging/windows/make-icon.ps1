# 生成 Dungeon Master 的程序图标：朱红圆角印章底 + 白色二十面骰（正面视图）。
# 用法：pwsh -File packaging/windows/make-icon.ps1
# 输出：src/LocalDm.App/Assets/app.ico（16/24/32/48/64/128/256，PNG 压缩条目）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $repo 'src\LocalDm.App\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Frame([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 64.0
    $seal = [System.Drawing.Color]::FromArgb(255, 0x94, 0x3f, 0x32)
    $r = 12 * $s; $d = $r * 2; $m = 2 * $s; $w = $size - 2 * $m
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new($seal), $path)

    # 骰子：中心 (32,33)，外接半径 21；小尺寸只画轮廓和中间三角，免得糊成一片。
    $cx = 32 * $s; $cy = 33 * $s; $R = 21 * $s
    $toPoints = { param([double[]]$xy) for ($k = 0; $k -lt $xy.Length; $k += 2) { [System.Drawing.PointF]::new($cx + $xy[$k] * $R, $cy + $xy[$k + 1] * $R) } }
    [System.Drawing.PointF[]]$hex = & $toPoints @(0, -1, 0.866, -0.5, 0.866, 0.5, 0, 1, -0.866, 0.5, -0.866, -0.5)
    [System.Drawing.PointF[]]$tri = & $toPoints @(0, -0.5, 0.5, 0.36, -0.5, 0.36)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.2, 3 * $s))
    $pen.LineJoin = 'Round'
    $g.DrawPolygon($pen, $hex)
    $g.DrawPolygon($pen, $tri)
    if ($size -ge 32) {
        $thin = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1, 2 * $s))
        foreach ($pair in @(@(0, 0), @(1, 0), @(1, 1), @(2, 1), @(3, 1), @(3, 2), @(4, 2), @(5, 2), @(5, 0))) {
            $g.DrawLine($thin, $hex[$pair[0]], $tri[$pair[1]])
        }
    }
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { , (Frame $_) }
$fs = [System.IO.File]::Create($out)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
Write-Host "icon: $out"
