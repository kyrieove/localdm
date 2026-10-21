# 把不入库的私有合成真值复制到当前检出。
#
# fixtures/synthetic/.private/ 按约定不入库：合成材料的本地真值。
# 新开的工作树里没有它，ImportCoverageTests 里那条「公开就绪视图不泄露秘密」的测试
# 就会因为找不到文件而失败。那不是代码坏了，是真值没跟过来 ——
# 跑这个脚本补一份，别去删测试、也别加 Skip 让它变绿。
#
#   pwsh -File scripts/sync-local-fixtures.ps1
#   pwsh -File scripts/sync-local-fixtures.ps1 -Source D:\2\dev\dm
#
# 找不到来源时，脚本会告诉怎么从零生成（需要本机有 python 与 PyMuPDF）。
param(
    # 真值来源检出。留空时在仓库根目录的同级目录里找。
    [string]$Source = ""
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetPrivate = Join-Path $repoRoot 'fixtures\synthetic\.private'
$relativePrivate = 'fixtures\synthetic\.private'

function Test-HasGroundTruth([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    return Test-Path (Join-Path $root (Join-Path $relativePrivate 'expected.json'))
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    # 同级检出优先：工作树通常与主检出平级（D:\2\dev\dm 与 D:\2\dev\dm-<agent>）。
    $parent = Split-Path -Parent $repoRoot
    $candidates = Get-ChildItem -Path $parent -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq (Split-Path -Leaf $repoRoot) -or $_.Name -like 'dm*' } |
        Select-Object -ExpandProperty FullName

    $Source = $candidates | Where-Object { Test-HasGroundTruth $_ } | Select-Object -First 1
}

if (-not (Test-HasGroundTruth $Source)) {
    Write-Host "找不到私有合成真值的来源。" -ForegroundColor Red
    Write-Host "做法一：在一份已经有真值的检出里跑这个脚本，并显式给来源，例如"
    Write-Host "    pwsh -File scripts/sync-local-fixtures.ps1 -Source D:\2\dev\dm"
    Write-Host "做法二：在本机有 python 与 PyMuPDF 时，从零生成（会一并重写已入库的 PDF 与图片，"
    Write-Host "    生成后用 git checkout 把已入库的文件还原，只保留 .private）："
    Write-Host "    python scripts/make-synthetic-fixtures.py"
    exit 1
}

New-Item -ItemType Directory -Force -Path $targetPrivate | Out-Null
Copy-Item -Path (Join-Path $Source (Join-Path $relativePrivate '*')) -Destination $targetPrivate -Recurse -Force

Write-Host "已从 $Source 复制私有合成真值到 $targetPrivate" -ForegroundColor Green
Get-ChildItem -Path $targetPrivate -File | ForEach-Object { Write-Host "  $($_.Name)" }
