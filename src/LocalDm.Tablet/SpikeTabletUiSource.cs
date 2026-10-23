using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>探针用的合成界面投影，真机由桌面换成真实投影。</summary>
public sealed class SpikeTabletUiSource(IPublicFeedSource feed) : ITabletUiSource
{
    public async Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken)
    {
        var snapshot = await feed.ReadAsync(after, cancellationToken);
        return new TabletUiProjection
        {
            SessionId = snapshot.SessionId,
            Title = "合成剧本",
            State = "Playing",
            Revision = 1,
            PhaseId = "phase-search",
            PhaseTitle = "搜证",
            PhaseIndex = 2,
            PhaseCount = 4,
            Hint = "合成提示：把线索卡摆开再讨论。",
            Phases =
            [
                new TabletUiPhaseItem { PhaseId = "phase-open", Title = "开场", Status = TabletUiPhaseStatus.Done },
                new TabletUiPhaseItem { PhaseId = "phase-search", Title = "搜证", Status = TabletUiPhaseStatus.Current },
                new TabletUiPhaseItem { PhaseId = "phase-talk", Title = "讨论", Status = TabletUiPhaseStatus.Upcoming },
                new TabletUiPhaseItem { PhaseId = "phase-reveal", Title = "揭秘", Status = TabletUiPhaseStatus.Upcoming },
            ],
            Controls =
            [
                new TabletUiControl { Kind = TabletUiControlKind.Start, Label = "开始这一局", Visible = false, Enabled = false },
                new TabletUiControl { Kind = TabletUiControlKind.Advance, Label = "进入讨论", Visible = true, Enabled = true },
                new TabletUiControl { Kind = TabletUiControlKind.ReleaseMaterials, Label = "发放材料 card-3", Visible = true, Enabled = true },
                new TabletUiControl { Kind = TabletUiControlKind.SubmitVote, Label = "确认提交汇总", Visible = false, Enabled = false },
                new TabletUiControl { Kind = TabletUiControlKind.Reveal, Label = "公开真相", Visible = false, Enabled = false },
                new TabletUiControl { Kind = TabletUiControlKind.Complete, Label = "进入复盘", Visible = false, Enabled = false },
                new TabletUiControl { Kind = TabletUiControlKind.PauseResume, Label = "暂停本局", Visible = true, Enabled = true },
            ],
            ControlsNote = "请在电脑上操作",
            PendingMaterialCount = 2,
            ReleasedMaterialCount = 1,
            VoteSummary = null,
            RevealedTexts = null,
            Messages = snapshot.Messages,
            NextSequence = snapshot.NextSequence,
            HostBusy = hostBusy,
            HostSpeaking = hostSpeaking,
        };
    }
}
