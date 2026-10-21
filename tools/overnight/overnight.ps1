<#
.SYNOPSIS
  夜间自动跑：每个编码助手在自己的 git worktree 里按队列做任务。
  脚本负责门禁（构建 + 两组测试）、失败重试、失败隔离、最后试合并和早上的报告。

.DESCRIPTION
  只提交到各助手自己的 agent/* 分支和 overnight/integration 分支；
  从不合并、不推送 main，也不推送任何分支。早上由人和 Codex 审查后再合。

  用法（在主文件夹 D:\2\dev\dm 下，Windows PowerShell 5.1 或 PowerShell 7 均可）：
    powershell -ExecutionPolicy Bypass -File tools\overnight\overnight.ps1 -Check   # 睡前自检
    powershell -ExecutionPolicy Bypass -File tools\overnight\overnight.ps1          # 正式开跑
#>
param(
    [string]$Config = (Join-Path $PSScriptRoot 'agents.json'),
    [string]$Agent,            # 内部用：子进程只跑这一个助手
    [switch]$Check,            # 睡前自检：配置、git、dotnet、每个助手能否无交互运行
    [switch]$SkipIntegration   # 不做最后的试合并
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

if (-not (Test-Path $Config)) {
    throw "找不到配置 $Config。先把 agents.example.json 复制成 agents.json 并填好助手命令。"
}
$cfg = Get-Content -Raw -Encoding UTF8 $Config | ConvertFrom-Json
$runDir = $cfg.runDir
$logDir = Join-Path $runDir 'logs'
$stateDir = Join-Path $runDir 'state'
New-Item -ItemType Directory -Force -Path $runDir, $logDir, $stateDir | Out-Null

function Write-Utf8([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}

function Add-Utf8([string]$path, [string]$text) {
    [IO.File]::AppendAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}

function Say([string]$text) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text)
}

# 运行 git，失败时抛错并返回输出。用简单函数接收参数，免得 -q、-m 这类开关被 PowerShell 当成自己的参数。
function Git {
    $dir = $args[0]; $rest = @($args | Select-Object -Skip 1)
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 会把 git 写到 stderr 的进度当成错误
    $out = & git -C $dir @rest 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "git $($rest -join ' ') 失败（$LASTEXITCODE）：$($out -join ' ')" }
    return $out
}

# 运行 git，只返回退出码，不抛错。
function GitCode {
    $dir = $args[0]; $rest = @($args | Select-Object -Skip 1)
    $ErrorActionPreference = 'Continue'
    & git -C $dir @rest 2>&1 | Out-Null
    return $LASTEXITCODE
}

function Join-Args([string[]]$list) {
    if (-not $list) { return '' }
    ($list | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

# 带超时运行一个外部程序，输出写到 $logBase.out.log / .err.log。超时就连同子进程一起结束。
function Invoke-Timed {
    param([string]$Exe, [string[]]$ArgList, [string]$WorkDir, [string]$StdinFile, [string]$LogBase, [int]$Minutes)
    $p = @{
        FilePath               = $Exe
        WorkingDirectory       = $WorkDir
        RedirectStandardOutput = "$LogBase.out.log"
        RedirectStandardError  = "$LogBase.err.log"
        PassThru               = $true
        NoNewWindow            = $true
    }
    $joined = Join-Args $ArgList
    if ($joined) { $p.ArgumentList = $joined }
    if ($StdinFile) { $p.RedirectStandardInput = $StdinFile }
    $proc = Start-Process @p
    $null = $proc.Handle   # Windows PowerShell 5.1 不先取句柄就拿不到退出码
    if (-not $proc.WaitForExit($Minutes * 60000)) {
        $ErrorActionPreference = 'Continue'
        & taskkill /T /F /PID $proc.Id 2>&1 | Out-Null
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Out = "$LogBase.out.log"; Err = "$LogBase.err.log" }
    }
    $proc.WaitForExit()
    [pscustomobject]@{ ExitCode = $proc.ExitCode; TimedOut = $false; Out = "$LogBase.out.log"; Err = "$LogBase.err.log" }
}

function Get-Tail([string]$path, [int]$lines) {
    if (Test-Path $path) { (Get-Content -Encoding UTF8 -Tail $lines $path) -join "`n" } else { '' }
}

# CLAUDE.md 要求的三条命令。多个助手共用一把锁轮流跑，免得界面测试互相抢窗口。
function Invoke-Gate([string]$WorkDir, [string]$LogBase) {
    $steps = @(
        @('build', 'build', 'LocalDm.slnx'),
        @('tests', 'test', 'tests/LocalDm.Tests'),
        @('uitests', 'test', 'tests/LocalDm.UiTests')
    )
    $mutex = New-Object Threading.Mutex($false, 'Local\LocalDmOvernightGate')
    try { [void]$mutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
    try {
        foreach ($s in $steps) {
            $r = Invoke-Timed -Exe 'dotnet' -ArgList @($s[1], $s[2]) -WorkDir $WorkDir -LogBase "$LogBase.$($s[0])" -Minutes $cfg.gateTimeoutMinutes
            if ($r.ExitCode -ne 0) {
                $why = if ($r.TimedOut) { '超时' } else { "退出码 $($r.ExitCode)" }
                return [pscustomobject]@{ Ok = $false; Step = "dotnet $($s[1]) $($s[2])（$why）"; Log = $r.Out }
            }
        }
    }
    finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
    [pscustomobject]@{ Ok = $true; Step = ''; Log = '' }
}

# 规矩检查：不准删测试、不准新加跳过、不准碰剧本材料目录、不准换分支。
function Test-Guards([string]$WorkDir, [string]$Base, [string]$Branch) {
    $problems = @()
    $current = (Git $WorkDir rev-parse --abbrev-ref HEAD) | Select-Object -First 1
    if ($current -ne $Branch) {
        $problems += "助手把分支切到了 $current（应为 $Branch）"
        GitCode $WorkDir checkout $Branch | Out-Null
    }
    Git $WorkDir add -A | Out-Null
    $deleted = Git $WorkDir diff --cached --name-only --diff-filter=D $Base '--' tests
    if ($deleted) { $problems += "删除了测试文件：$($deleted -join '、')" }
    $skips = Git $WorkDir diff --cached -U0 $Base '--' tests |
        Where-Object { $_ -match '^\+(?!\+\+)' -and $_ -match '(Skip\s*=|Skip\.(If|Unless)|Assert\.Skip|\[Ignore)' }
    if ($skips) { $problems += "新增了跳过测试的写法：$(@($skips).Count) 处" }
    $private = Git $WorkDir diff --cached --name-only $Base |
        Where-Object { $_ -match '^(stories|\.appdata|\.local)/' }
    if ($private) { $problems += "改动了剧本材料目录：$($private -join '、')" }
    return , $problems
}

function Get-Tasks([string]$Name) {
    $dirs = @(Join-Path $PSScriptRoot "queue\$Name")
    if ($cfg.includeGated) { $dirs += Join-Path $PSScriptRoot "queue-gated\$Name" }
    $dirs | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem -File -Filter '*.md' $_ } | Sort-Object Name
}

function New-Prompt([string]$TaskFile, [string]$WorkDir, [string]$Branch, [string]$Feedback) {
    $rules = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'rules.md')
    $rules = $rules.Replace('{WORKTREE}', $WorkDir).Replace('{BRANCH}', $Branch)
    $task = Get-Content -Raw -Encoding UTF8 $TaskFile
    $text = "$rules`n`n---`n`n$task"
    if ($Feedback) { $text += "`n`n---`n`n## 上一次尝试没有通过，请先修好这些问题`n`n$Feedback" }
    return $text
}

function Invoke-AgentQueue($a) {
    $wt = $a.worktree
    $report = Join-Path $runDir "report-$($a.name).md"
    $doneFile = Join-Path $stateDir "$($a.name).done.txt"
    $done = @(); if (Test-Path $doneFile) { $done = @(Get-Content -Encoding UTF8 $doneFile) }

    foreach ($t in (Get-Tasks $a.name)) {
        if ($done -contains $t.Name) { Say "$($a.name)：$($t.Name) 已跑过，跳过"; continue }
        $base = (Git $wt rev-parse HEAD) | Select-Object -First 1
        $taskLog = Join-Path $logDir "$($a.name)-$($t.BaseName)"
        $feedback = ''
        $result = '失败'
        $detail = ''
        $agentExit = ''

        for ($i = 1; $i -le $cfg.maxAttempts; $i++) {
            Say "$($a.name)：$($t.Name) 第 $i 次尝试"
            $promptFile = "$taskLog-a$i.prompt.md"
            Write-Utf8 $promptFile (New-Prompt $t.FullName $wt $a.branch $feedback)
            $env:LOCALDM_PROMPT_FILE = $promptFile
            $argList = @($a.args | ForEach-Object { $_.Replace('{PROMPT_FILE}', $promptFile) })
            $stdin = $null; if ($a.stdinPrompt) { $stdin = $promptFile }
            $r = Invoke-Timed -Exe $a.exe -ArgList $argList -WorkDir $wt -StdinFile $stdin -LogBase "$taskLog-a$i.agent" -Minutes $cfg.agentTimeoutMinutes
            $agentExit = if ($r.TimedOut) { '超时被结束' } else { "退出码 $($r.ExitCode)" }

            # 助手按规矩停下来提问：记下问题，不再重试，改动按失败隔离。
            $question = Join-Path $wt 'OVERNIGHT-QUESTION.md'
            if (Test-Path $question) {
                $saved = "$taskLog-question.md"
                Move-Item -Force $question $saved
                $result = '提问'
                $detail = "助手停下来提问，见 $saved"
                break
            }

            $problems = Test-Guards $wt $base $a.branch
            $staged = (GitCode $wt diff --cached --quiet) -ne 0
            $head = (Git $wt rev-parse HEAD) | Select-Object -First 1
            if (-not $staged -and $head -eq $base -and $problems.Count -eq 0) {
                $result = '无改动'; $detail = "助手没有改任何文件（$agentExit），见 $taskLog-a$i.agent.out.log"
                break
            }
            if ($problems.Count -gt 0) {
                $feedback = "脚本检查发现违反规矩：`n- " + ($problems -join "`n- ") + "`n请撤回这些改动，按规矩重做。"
                $detail = $problems -join '；'
                continue
            }
            $gate = Invoke-Gate $wt "$taskLog-a$i"
            if (-not $gate.Ok) {
                $feedback = "门禁失败的一步：$($gate.Step)`n日志末尾：`n``````n$(Get-Tail $gate.Log 150)`n``````"
                $detail = "门禁失败：$($gate.Step)，日志 $($gate.Log)"
                continue
            }
            Git $wt add -A | Out-Null
            if ((GitCode $wt diff --cached --quiet) -ne 0) {
                Git $wt commit -q -m "$($t.BaseName)：夜间自动提交（构建与两组测试已通过）" | Out-Null
            }
            $result = '成功'
            $detail = "第 $i 次通过；提交 $base..$((Git $wt rev-parse --short HEAD) | Select-Object -First 1)"
            break
        }

        if ($result -eq '失败' -or $result -eq '提问') {
            # 失败的尝试保留在 failed/ 分支上，助手分支退回任务开始前，下一个任务不受影响。
            Git $wt add -A | Out-Null
            if ((GitCode $wt diff --cached --quiet) -ne 0) { Git $wt commit -q --no-verify -m "未通过门禁的尝试：$($t.BaseName)" | Out-Null }
            $head = (Git $wt rev-parse HEAD) | Select-Object -First 1
            if ($head -ne $base) {
                $failed = "failed/$($a.name)/$($t.BaseName)"
                Git $wt branch -f $failed $head | Out-Null
                Git $wt reset -q --hard $base | Out-Null
                $detail += "；失败的改动留在分支 $failed"
            }
        }
        Add-Utf8 $report ("| {0} | {1} | {2} | {3} | {4} |`n" -f $a.name, $t.Name, $result, $agentExit, $detail)
        Add-Utf8 $doneFile "$($t.Name)`n"
        Say "$($a.name)：$($t.Name) → $result"
    }
}

function Set-Worktree($a) {
    if (Test-Path (Join-Path $a.worktree '.git')) { return }
    $exists = (GitCode $cfg.repo show-ref --verify --quiet "refs/heads/$($a.branch)") -eq 0
    if ($exists) { Git $cfg.repo worktree add $a.worktree $a.branch | Out-Null }
    else { Git $cfg.repo worktree add $a.worktree -b $a.branch $cfg.baseBranch | Out-Null }
    Say "已建工作文件夹 $($a.worktree)（分支 $($a.branch)）"
}

function Invoke-Integration {
    $wt = $cfg.integrationWorktree
    $branch = 'overnight/integration'
    if (-not (Test-Path (Join-Path $wt '.git'))) {
        Git $cfg.repo worktree add -f -B $branch $wt $cfg.baseBranch | Out-Null
    }
    else {
        Git $wt checkout -q -B $branch $cfg.baseBranch | Out-Null
        Git $wt reset -q --hard $cfg.baseBranch | Out-Null
    }
    $lines = @()
    foreach ($a in $cfg.agents) {
        $ahead = (Git $wt rev-list --count "$($cfg.baseBranch)..$($a.branch)") | Select-Object -First 1
        if ([int]$ahead -eq 0) { $lines += "- $($a.branch)：没有新提交"; continue }
        if ((GitCode $wt merge --no-ff --no-edit $a.branch) -ne 0) {
            GitCode $wt merge --abort | Out-Null
            $lines += "- $($a.branch)：和前面的分支冲突，没合进来，早上手动处理"
        }
        else { $lines += "- $($a.branch)：已合入（$ahead 个提交）" }
    }
    $gate = Invoke-Gate $wt (Join-Path $logDir 'integration')
    $verdict = if ($gate.Ok) { '三条命令全部通过' } else { "未通过：$($gate.Step)，日志 $($gate.Log)" }
    return "## 试合并（分支 $branch，文件夹 $wt）`n`n$($lines -join "`n")`n`n门禁：$verdict`n"
}

function Invoke-Check {
    $ok = $true
    foreach ($tool in 'git', 'dotnet') {
        if (Get-Command $tool -ErrorAction SilentlyContinue) { Say "找到 $tool" } else { Say "缺少 $tool"; $ok = $false }
    }
    $dirty = Git $cfg.repo status --porcelain --untracked-files=no
    if ($dirty) { Say "提醒：主文件夹有未提交的改动，新工作文件夹不会带上它们" }
    foreach ($a in $cfg.agents) {
        if ($a.exe -like '填*') { Say "$($a.name)：agents.json 里的 exe 还没填"; $ok = $false; continue }
        Set-Worktree $a
        Say "$($a.name)：队列 $(@(Get-Tasks $a.name).Count) 个任务；试跑一次无交互模式（最多 5 分钟）"
        $probe = Join-Path $logDir "check-$($a.name).prompt.md"
        Write-Utf8 $probe '这是连通性测试。不要读取或修改任何文件，不要运行任何命令，只回复 OK。'
        $env:LOCALDM_PROMPT_FILE = $probe
        $argList = @($a.args | ForEach-Object { $_.Replace('{PROMPT_FILE}', $probe) })
        $stdin = $null; if ($a.stdinPrompt) { $stdin = $probe }
        $r = Invoke-Timed -Exe $a.exe -ArgList $argList -WorkDir $a.worktree -StdinFile $stdin -LogBase (Join-Path $logDir "check-$($a.name)") -Minutes 5
        $reply = Get-Tail $r.Out 5
        if ($r.ExitCode -eq 0 -and $reply -match 'OK') { Say "$($a.name)：通过" }
        else {
            $ok = $false
            Say "$($a.name)：没通过（$(if ($r.TimedOut) { '超时，可能在等人确认' } else { "退出码 $($r.ExitCode)" })），看 $($r.Out) 和 $($r.Err)"
        }
        if (Git $a.worktree status --porcelain) { Say "$($a.name)：试跑后文件夹里有改动，检查一下" ; $ok = $false }
    }
    if ($ok) { Say '自检通过，可以开跑。' } else { Say '自检没通过，先按上面的提示修好。'; exit 1 }
}

# ---------- 入口 ----------

if ($Agent) {
    $a = $cfg.agents | Where-Object { $_.name -eq $Agent }
    Invoke-AgentQueue $a
    exit 0
}

if ($Check) { Invoke-Check; exit 0 }

foreach ($a in $cfg.agents) {
    if ($a.exe -like '填*') { throw "agents.json 里 $($a.name) 的 exe 还没填，先跑 -Check。" }
}

# 跑的过程中不让电脑睡眠（脚本结束后自动恢复）。
Add-Type -Namespace LocalDm -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][LocalDm.Power]::SetThreadExecutionState([uint32]2147483649)

$started = Get-Date
Say "开始夜间运行，日志在 $logDir"
$procs = @()
foreach ($a in $cfg.agents) {
    Set-Worktree $a
    $host_ = (Get-Process -Id $PID).Path
    $childArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Config `"$Config`" -Agent $($a.name)"
    $procs += Start-Process -FilePath $host_ -ArgumentList $childArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logDir "runner-$($a.name).out.log") `
        -RedirectStandardError (Join-Path $logDir "runner-$($a.name).err.log")
}
$procs | ForEach-Object { $_.WaitForExit() }

$summary = "# 夜间运行报告`n`n开始 $started，结束 $(Get-Date)`n`n| 助手 | 任务 | 结果 | 助手进程 | 说明 |`n|---|---|---|---|---|`n"
foreach ($a in $cfg.agents) {
    $f = Join-Path $runDir "report-$($a.name).md"
    if (Test-Path $f) { $summary += Get-Content -Raw -Encoding UTF8 $f }
    $err = Join-Path $logDir "runner-$($a.name).err.log"
    if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) { $summary += "| $($a.name) | （运行脚本出错） | 见 $err | | |`n" }
}
if (-not $SkipIntegration) { $summary += "`n" + (Invoke-Integration) }
$summary += "`n没有合并或推送 main。早上审查后再按 tools/overnight/README.md 第 5 步合并。`n"
Write-Utf8 (Join-Path $runDir 'report.md') $summary
[void][LocalDm.Power]::SetThreadExecutionState([uint32]2147483648)
Say "全部结束，报告：$(Join-Path $runDir 'report.md')"
