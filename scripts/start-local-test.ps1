param([switch]$NoOpen)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot 'artifacts/publish/win-x64/LocalDm.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw '还没有发布版，请先运行 pwsh -File packaging/windows/Publish.ps1。'
}
if (-not (Get-Process LocalDm -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
}
Write-Host '已打开本局主持发布版。'
Write-Host '从剧本库选择真实剧本，在「偏好设置 → 平板连接」开启连接，即可看到本局网址和二维码。'
Write-Host '启动器不会另开合成材料网页，也不会重新编译。'
