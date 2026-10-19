# Script to mark tasks complete in tasks.md while keeping the
# `- [ ] Tnnn ...` line format that scripts/validate-planning.ps1 enforces.
#
# The format check uses the literal unchecked box, so completion is recorded
# in the tasks.md "实施状态" table (T00x -> 状态 + 证据) and the boxes stay `[ ]`.
# This script rewrites that status table from a source-of-truth mapping below.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tasksPath = Join-Path $root 'specs/001-local-dm-mvp/tasks.md'

$status = @{
    'T001' = @('已完成', 'docs/evidence/setup/environment.md')
    'T002' = @('已完成', 'LocalDm.slnx, global.json, Directory.Packages.props, packages.lock.json')
    'T003' = @('已完成', '.gitignore（stories/密钥/数据库/构建产物/合成真值）')
    'T004' = @('已完成', 'fixtures/synthetic + scripts/make-synthetic-fixtures.py')
    'T005' = @('已完成', 'tests/LocalDm.Tests, tests/LocalDm.UiTests')
    'T006' = @('已完成', 'docs/evidence/README.md')
    'T007' = @('已完成', 'src/LocalDm.Core/Domain/SourceModels.cs')
    'T008' = @('已完成', 'src/LocalDm.Core/Domain/ToolSettings.cs')
    'T009' = @('已完成', 'src/LocalDm.Core/Ports/ExternalPorts.cs')
    'T010' = @('已完成', 'src/LocalDm.Core/Domain/PublicProjection.cs')
    'T011' = @('已完成', 'Storage/SessionStore.cs + SqliteSchemaSql.cs（SessionStoreTests 7 项）')
    'T012' = @('已完成', 'src/LocalDm.Core/Domain/HostModels.cs')
    'T013' = @('已完成', 'src/LocalDm.Core/Domain/ImportModels.cs')
    'T014' = @('已完成', 'AppComposition.cs + Diagnostics/RedactedLog.cs')
    'T015' = @('已完成', 'tests/LocalDm.Tests/MagpieGatewayTests.cs（8 项）')
    'T016' = @('已完成', 'tests/LocalDm.Tests/SpeechLifecycleTests.cs（5 项）')
    'T017' = @('已完成', 'src/LocalDm.Infrastructure/Magpie/MagpieGateway.cs')
    'T018' = @('已完成', 'src/LocalDm.Infrastructure/Storage/SettingsStore.cs（DPAPI）')
    'T019' = @('已完成', 'src/LocalDm.Infrastructure/Voice/TtsService.cs（TtsServiceTests 4 项）')
    'T020' = @('已完成', 'src/LocalDm.Core/Hosting/HostDirector.cs')
    'T021' = @('已完成', 'Views/SetupView.xaml + SetupViewModel.cs')
    'T022' = @('已完成', 'Views/HostView.xaml + ViewModels/HostViewModel.cs')
    'T023' = @('已完成', 'Views/LibraryView.xaml + MainWindow.xaml')
    'T024' = @('部分完成', 'docs/evidence/us1/desktop-speech.md（125%/150% 缩放未截图）')
    'T025' = @('已完成', 'tests/LocalDm.Tests/DocumentToolContractTests.cs（10 项）')
    'T026' = @('已完成', 'tests/LocalDm.Tests/ImportCoverageTests.cs（11 项）')
    'T027' = @('已完成', 'src/LocalDm.Infrastructure/Documents/MineruPrivacyPreflight.cs')
    'T028' = @('部分完成', '设置页含 MinerU 路径字段；独立 profile 配置页未做')
    'T029' = @('已完成', 'src/LocalDm.Infrastructure/Documents/MineruRunner.cs')
    'T030' = @('已完成', 'src/LocalDm.Infrastructure/Documents/MineruContinuationReader.cs')
    'T031' = @('已完成', 'src/LocalDm.Infrastructure/Import/SourceIndex.cs')
    'T032' = @('已完成', 'src/LocalDm.Core/Import/PreparedPackageReader.cs')
    'T033' = @('已完成', 'src/LocalDm.Infrastructure/Import/ImportPipeline.cs')
    'T034' = @('已完成', 'src/LocalDm.Core/Import/ScriptValidator.cs + ScriptPackageBuilder')
    'T035' = @('部分完成', 'ReadinessProjection.cs 已完成；ImportView/ReadinessView 界面未做')
    'T036' = @('受阻', 'docs/evidence/us2/tool-parse.md（quality_tier_unavailable）')
    'T037' = @('受阻', 'docs/evidence/us2/real-script-readiness.md')
    'T038' = @('已完成', 'tests/LocalDm.Tests/PhaseFlowTests.cs（16 项）')
    'T039' = @('已完成', 'tests/LocalDm.Tests/IdempotencyTests.cs + RecoveryTests.cs')
    'T040' = @('已完成', 'src/LocalDm.Core/Domain/GameModels.cs')
    'T041' = @('已完成', 'src/LocalDm.Core/Game/GameEngine.cs')
    'T042' = @('已完成', 'src/LocalDm.Core/Game/CommandDispatcher.cs + SessionStore.cs')
    'T043' = @('已完成', 'src/LocalDm.Core/Game/PublicSessionProjector.cs')
    'T044' = @('已完成', 'HostView 阶段面板：无剧透条件 + 大家准备勾选')
    'T045' = @('已完成', 'HostView 纸笔汇总录入 + 二次确认（SessionViewModel.TryParseVotes）')
    'T046' = @('已完成', 'src/LocalDm.Core/Hosting/PublicContextBuilder.cs + PublicReplyPolicy')
    'T047' = @('已完成', 'HostViewModel 接入真实 session 与阶段操作')
    'T048' = @('已完成', 'src/LocalDm.Infrastructure/Storage/SessionRecovery.cs + 继续一局')
    'T049' = @('已完成', 'docs/evidence/us3/flow-recovery.md + ui-session-steps.json')
    'T050' = @('已完成', 'tests/LocalDm.Tests/TextSubmissionTests.cs（7 项）')
    'T051' = @('已完成', 'src/LocalDm.App/Input/HostInputController.cs')
    'T052' = @('已完成', 'src/LocalDm.Core/Hosting/HostIntentRouter.cs')
    'T053' = @('已完成', 'HostView 输入提示 + 提案提示')
    'T054' = @('已完成', 'tests/LocalDm.Tests/HostUncertaintyTests.cs（10 项）')
    'T055' = @('部分完成', 'docs/evidence/us4/openless-input.md（真实语音与 20 次压力未执行）')
    'T056' = @('已完成', 'tests/LocalDm.UiTests/ReleaseSmokeTests.cs（2 项，真实 WPF 窗口）')
    'T057' = @('已完成', 'src/LocalDm.App/Properties/PublishProfiles/WinX64.pubxml')
    'T058' = @('已完成', 'packaging/windows/LocalDm.iss（per-user，卸载保留数据）')
    'T059' = @('已完成', 'packaging/windows/Publish.ps1（Setup + ZIP）')
    'T060' = @('部分完成', '设置页有 Magpie/音色/MinerU 诊断；OpenLess 无独立诊断项')
    'T061' = @('已完成', 'docs/evidence/us5/install-portable.md + portable-smoke.json')
    'T062' = @('未执行', 'docs/evidence/us5/real-tabletop.md（需真人到场）')
    'T063' = @('已完成', 'docs/evidence/us5/fault-matrix.md + FaultMatrixTests（4 项）')
    'T064' = @('已完成', 'docs/user-guide.md')
    'T065' = @('已完成', 'docs/release-checklist.md')
    'T066' = @('已完成', 'docs/evidence/final-validation.md')
    'T067' = @('已完成', 'docs/project-state.md 已更新并本地提交')
}

$text = Get-Content -Raw -LiteralPath $tasksPath
$block = @()
$block += ''
$block += '## 实施状态（2026-10-19 本轮实施后）'
$block += ''
$block += '`scripts/validate-planning.ps1` 强制每行保持 `- [ ] Tnnn ...` 字面格式，因此勾选框不在此改动；'
$block += '实际完成状态以下表为准。状态取值：已完成 / 部分完成 / 受阻 / 未执行。'
$block += ''
$block += '| 任务 | 状态 | 证据或差异 |'
$block += '|---|---|---|'
foreach ($id in ($status.Keys | Sort-Object)) {
    $row = $status[$id]
    $block += "| $id | $($row[0]) | $($row[1]) |"
}
$block += ''
$block += '统计：已完成 59 项，部分完成 5 项（T024/T028/T035/T055/T060），受阻 2 项（T036/T037），未执行 1 项（T062）。'
$block += '部分完成与受阻项的差异均已写入对应 docs/evidence 文件，未以自动化替代真人验收。'

$marker = '## 实施状态（2026-10-19 本轮实施后）'
$index = $text.IndexOf($marker, [StringComparison]::Ordinal)
if ($index -ge 0) {
    $text = $text.Substring(0, $index).TrimEnd() + "`r`n" + ($block -join "`r`n") + "`r`n"
}
else {
    $text = $text.TrimEnd() + "`r`n" + ($block -join "`r`n") + "`r`n"
}

Set-Content -LiteralPath $tasksPath -Value $text -Encoding utf8NoBOM -NoNewline
Write-Host "updated $tasksPath with $($status.Count) task statuses"
