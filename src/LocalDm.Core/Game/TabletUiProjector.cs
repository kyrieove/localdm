using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;

namespace LocalDm.Core.Game;

public static class TabletUiProjector
{
    public const string ControlsNote = "请在电脑上操作";

    public static TabletUiProjection Project(
        ScriptPackage package,
        Session session,
        PublicFeedSnapshot feed,
        bool hostBusy,
        bool hostSpeaking)
    {
        if (!string.Equals(feed.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "公开消息不属于本局。");
        }

        var p = PublicSessionProjector.Project(package, session);
        var phase = package.Phases.First(x => x.PhaseId == p.CurrentPhase.PhaseId);

        var releasedSet = new HashSet<string>(p.ReleasedMaterialIds, StringComparer.Ordinal);
        var pending = phase.MaterialIds
            .Where(id => !releasedSet.Contains(id))
            .ToList();

        var playing = string.Equals(p.State, nameof(SessionState.Playing), StringComparison.Ordinal);

        var startVisible = string.Equals(p.State, nameof(SessionState.Prepared), StringComparison.Ordinal);
        var start = new TabletUiControl
        {
            Kind = TabletUiControlKind.Start,
            Label = "开始这一局",
            Visible = startVisible,
            Enabled = startVisible && !hostBusy,
        };

        var advanceVisible = playing
            && p.CurrentPhase.AllowedActions.Contains(PublicAction.Advance)
            && !(phase.IsVotePhase && p.VoteSummary is null)
            && !phase.IsRevealPhase;
        var advanceLabel = !string.IsNullOrWhiteSpace(p.CurrentPhase.AdvanceLabel)
            ? p.CurrentPhase.AdvanceLabel!
            : "结束本局";
        var advanceEnabled = advanceVisible
            && !hostBusy
            && !(p.CurrentPhase.RequiresAllMaterials && pending.Count > 0);
        var advance = new TabletUiControl
        {
            Kind = TabletUiControlKind.Advance,
            Label = advanceLabel,
            Visible = advanceVisible,
            Enabled = advanceEnabled,
        };

        var releaseVisible = playing
            && p.CurrentPhase.AllowedActions.Contains(PublicAction.ReleaseMaterials)
            && pending.Count > 0;
        var releaseLabel = pending.Count > 0
            ? $"发放材料 {pending[0]}"
            : "发放材料";
        var releaseMaterials = new TabletUiControl
        {
            Kind = TabletUiControlKind.ReleaseMaterials,
            Label = releaseLabel,
            Visible = releaseVisible,
            Enabled = releaseVisible && !hostBusy,
        };

        var submitVoteVisible = playing && phase.IsVotePhase && p.VoteSummary is null;
        var submitVote = new TabletUiControl
        {
            Kind = TabletUiControlKind.SubmitVote,
            Label = "确认提交汇总",
            Visible = submitVoteVisible,
            Enabled = false,
        };

        var revealVisible = playing && phase.IsRevealPhase;
        var revealLabel = !string.IsNullOrWhiteSpace(p.CurrentPhase.RevealLabel)
            ? p.CurrentPhase.RevealLabel!
            : "公开真相";
        var reveal = new TabletUiControl
        {
            Kind = TabletUiControlKind.Reveal,
            Label = revealLabel,
            Visible = revealVisible,
            Enabled = revealVisible && !hostBusy,
        };

        var completeVisible = string.Equals(p.State, nameof(SessionState.Revealed), StringComparison.Ordinal)
            || (string.Equals(p.State, nameof(SessionState.Paused), StringComparison.Ordinal) && p.RevealedTexts is { Count: > 0 });
        var complete = new TabletUiControl
        {
            Kind = TabletUiControlKind.Complete,
            Label = "进入复盘",
            Visible = completeVisible,
            Enabled = completeVisible && !hostBusy,
        };

        var pauseResumeVisible = string.Equals(p.State, nameof(SessionState.Playing), StringComparison.Ordinal)
            || string.Equals(p.State, nameof(SessionState.Paused), StringComparison.Ordinal)
            || string.Equals(p.State, nameof(SessionState.Revealed), StringComparison.Ordinal);
        var pauseResumeLabel = string.Equals(p.State, nameof(SessionState.Paused), StringComparison.Ordinal)
            ? "继续本局"
            : "暂停本局";
        var pauseResume = new TabletUiControl
        {
            Kind = TabletUiControlKind.PauseResume,
            Label = pauseResumeLabel,
            Visible = pauseResumeVisible,
            Enabled = pauseResumeVisible && !hostBusy,
        };

        var controls = new TabletUiControl[]
        {
            start,
            advance,
            releaseMaterials,
            submitVote,
            reveal,
            complete,
            pauseResume,
        };

        var currentPhaseIndex = -1;
        for (var i = 0; i < p.PhaseTrack.Count; i++)
        {
            if (string.Equals(p.PhaseTrack[i].PhaseId, p.CurrentPhase.PhaseId, StringComparison.Ordinal))
            {
                currentPhaseIndex = i;
                break;
            }
        }

        var phases = new List<TabletUiPhaseItem>(p.PhaseTrack.Count);
        for (var i = 0; i < p.PhaseTrack.Count; i++)
        {
            var item = p.PhaseTrack[i];
            var status = i < currentPhaseIndex
                ? TabletUiPhaseStatus.Done
                : i == currentPhaseIndex
                    ? TabletUiPhaseStatus.Current
                    : TabletUiPhaseStatus.Upcoming;

            phases.Add(new TabletUiPhaseItem
            {
                PhaseId = item.PhaseId,
                Title = item.Title,
                Status = status,
            });
        }

        return new TabletUiProjection
        {
            SessionId = session.SessionId,
            Title = p.PublicTitle,
            State = p.State,
            Revision = p.Revision,
            PhaseId = p.CurrentPhase.PhaseId,
            PhaseTitle = p.CurrentPhase.Title,
            PhaseIndex = p.CurrentPhase.Index,
            PhaseCount = p.CurrentPhase.Count,
            Hint = p.CurrentPhase.Hint,
            Phases = phases,
            Controls = controls,
            ControlsNote = ControlsNote,
            PendingMaterialCount = pending.Count,
            ReleasedMaterialCount = p.ReleasedMaterialIds.Count,
            VoteSummary = p.VoteSummary,
            RevealedTexts = p.RevealedTexts,
            Messages = feed.Messages,
            NextSequence = feed.NextSequence,
            HostBusy = hostBusy,
            HostSpeaking = hostSpeaking,
        };
    }
}
