param([switch]$NoOpen)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot 'artifacts/publish/win-x64/LocalDm.exe'

# 发布版比源码旧（或还没有）就先重新发布，保证启动的永远是最新代码。
$newest = Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -File -Include *.cs, *.xaml, *.razor, *.css, *.js, *.csproj, *.py |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not (Test-Path -LiteralPath $exe) -or ($newest -and $newest.LastWriteTime -gt (Get-Item -LiteralPath $exe).LastWriteTime)) {
    Write-Host '发布版不是最新，正在重新发布（约 1–2 分钟）……'
    Get-Process LocalDm -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process -Force
    & pwsh -NoProfile -File (Join-Path $repoRoot 'packaging/windows/Publish.ps1') -SkipInstaller | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw '重新发布失败。' }
}

if (-not (Get-Process LocalDm -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
}
Write-Host '已打开本局主持（最新发布版）。'
Write-Host '平板入口在剧本库页顶部：二维码和网址软件一打开就有。'
