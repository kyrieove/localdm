# 生成 LocalDm 的 Windows 发布物：自包含便携目录 + ZIP +（可选）安装器。
#
# 用法：
#   pwsh -File packaging/windows/Publish.ps1
#   pwsh -File packaging/windows/Publish.ps1 -SkipInstaller
#
# 明确不包含：stories/ 真实剧本、任何 caller key、运行数据库、日志。
# 运行数据在 %LOCALAPPDATA%\LocalDm，不在安装目录。

[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishDir = Join-Path $repo 'artifacts\publish\win-x64'
$artifacts = Join-Path $repo 'artifacts'
$zipPath = Join-Path $artifacts 'LocalDm-win-x64.zip'

# -r win-x64 会往 packages.lock.json 加一个 target 弄脏主树，发布后还原。
$locks = Get-ChildItem -Path $repo -Recurse -Filter packages.lock.json | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
$lockBytes = @{}
foreach ($l in $locks) { $lockBytes[$l.FullName] = [IO.File]::ReadAllBytes($l.FullName) }
Write-Host "==> dotnet publish (self-contained win-x64)" -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src\LocalDm.App\LocalDm.App.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=WinX64 `
    -p:PublishDir="$publishDir\" | Out-Host

foreach ($k in $lockBytes.Keys) { [IO.File]::WriteAllBytes($k, $lockBytes[$k]) }
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path (Join-Path $publishDir 'LocalDm.exe'))) { throw 'LocalDm.exe missing from publish output' }

# 明确清掉任何可能混入的私有内容
$forbidden = @('stories', '.appdata', 'sessions.db', 'settings.json', 'secrets')
foreach ($name in $forbidden) {
    Get-ChildItem -Path $publishDir -Recurse -Force -Filter $name -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Host "removing forbidden item: $($_.FullName)" -ForegroundColor Yellow
            Remove-Item $_.FullName -Recurse -Force
        }
}

Write-Host "==> portable zip" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "zip: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"

# 安装器：只有在本机装有 Inno Setup 时才构建，不自动下载
$iscc = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$setupPath = Join-Path $artifacts 'LocalDm-Setup-x64.exe'
if ($SkipInstaller) {
    Write-Host '==> installer skipped (-SkipInstaller)' -ForegroundColor Yellow
}
elseif (-not $iscc) {
    Write-Host '==> Inno Setup (ISCC.exe) not found; installer NOT built. Portable ZIP is available.' -ForegroundColor Yellow
    Write-Host '    Install Inno Setup 6 and re-run this script to produce LocalDm-Setup-x64.exe.'
}
else {
    Write-Host "==> building installer with $iscc" -ForegroundColor Cyan
    & $iscc "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'LocalDm.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
    Write-Host "installer: $setupPath"
}

Write-Host ''
Write-Host '=== 发布物清单 ===' -ForegroundColor Green
Get-ChildItem $artifacts -File | Select-Object Name, @{N = 'MB'; E = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-Host
Get-ChildItem $publishDir -File | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("publish dir: {0} files, {1:N1} MB" -f $_.Count, ($_.Sum / 1MB)) }
