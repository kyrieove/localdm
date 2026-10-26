# 设好 .tools\ 下的 Flutter / Android 环境变量，再把参数原样转给 flutter，在 apps/tablet 里运行。
# 用 PowerShell 跑：Git Bash 会把 `--base-href /app/` 这类参数改写成 Windows 路径。
# 例：pwsh apps/tablet/tool/flutter.ps1 build web --release --no-web-resources-cdn --base-href /app/
# .tools 只放在主工作区（几 GB，不进 git）；从 worktree 运行时按 git 公共目录找回主工作区。
$common = git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir
$tools = Join-Path (Split-Path $common -Parent) '.tools'
$env:PUB_CACHE = "$tools\pub-cache"
$env:ANDROID_HOME = "$tools\android-sdk"
$env:ANDROID_USER_HOME = "$tools\android-home"
$env:GRADLE_USER_HOME = "$tools\gradle"
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & "$tools\flutter\bin\flutter.bat" @args
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
