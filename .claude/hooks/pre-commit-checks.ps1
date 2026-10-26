# Claude Code PreToolUse hook：拦 git commit，按要提交的路径跑项目检查，不过就退出 2 挡住提交。
#   src/、tests/、解决方案与构建配置 → dotnet build + LocalDm.Tests + LocalDm.UiTests
#   apps/tablet/                   → flutter analyze + flutter test
# 只改文档、脚本时什么都不跑。
$hook = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$hook.tool_input.command
if ($command -notmatch '\bgit\b[^\n;|&]*\bcommit\b') { exit 0 }

# 命令里先 cd 或 git -C 到别的目录（例如 worktree）时，以那个目录为准；Git Bash 路径 /d/… 换成 D:/…。
$dir = $hook.cwd
if ($command -match '(?:\bcd|\bgit\s+-C)\s+("[^"]+"|\S+)') { $dir = $Matches[1].Trim('"') -replace '^/([a-zA-Z])/', '$1:/' }
$repo = git -C $dir rev-parse --show-toplevel 2>$null
if (-not $repo) { exit 0 }
$files = @(git -C $repo diff --cached --name-only)
# shortcut: 「git add … && git commit」一条命令里，add 还没执行，就把工作区改动也算上；在 worktree 里干活时这就是本轮改动。
if ($command -match '\bgit\b[^\n;|&]*\badd\b') {
    $files += git -C $repo diff --name-only
    $files += git -C $repo ls-files --others --exclude-standard
}

$dotnet = $files | Where-Object { $_ -match '^(src|tests)/|^LocalDm\.slnx$|^Directory\.(Build|Packages)\.props$|^global\.json$' }
$flutter = $files | Where-Object { $_ -match '^apps/tablet/(lib|test)/|^apps/tablet/pubspec\.' }

function Run([string]$label, [scriptblock]$step) {
    $output = & $step 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("提交前检查没过：$label`n" + ($output -split "`n" | Select-Object -Last 30 | Out-String))
        exit 2
    }
}

Push-Location $repo
try {
    if ($dotnet) {
        Run 'dotnet build LocalDm.slnx' { dotnet build LocalDm.slnx --nologo -v q }
        Run 'dotnet test tests/LocalDm.Tests' { dotnet test tests/LocalDm.Tests --no-build }
        Run 'dotnet test tests/LocalDm.UiTests' { dotnet test tests/LocalDm.UiTests --no-build }
    }
    if ($flutter) {
        Run 'flutter analyze' { pwsh -NoProfile -File apps/tablet/tool/flutter.ps1 analyze }
        Run 'flutter test' { pwsh -NoProfile -File apps/tablet/tool/flutter.ps1 test }
    }
} finally {
    Pop-Location
}
exit 0
