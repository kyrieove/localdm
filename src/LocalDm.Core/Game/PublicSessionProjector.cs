using LocalDm.Core.Domain;

namespace LocalDm.Core.Game;

/// <summary>
/// 公共会话投影：只投影允许公开的阶段、材料编号及揭秘内容。
/// 不把 truth 对象交给 HostView；秘密隔离发生在业务投影处，不靠 UI 隐藏。
/// </summary>
public static class PublicSessionProjector
{
    public static PublicSessionProjection Project(ScriptPackage package, Session session)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId)
                    ?? package.Phases.FirstOrDefault()
                    ?? throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "剧本包没有可用阶段。");

        var phaseIndex = package.Phases
            .Select((p, i) => (p.PhaseId, Index: i))
            .FirstOrDefault(t => t.PhaseId == phase.PhaseId).Index;

        // 只有 Public 段允许出现在公共投影里；Private / Truth 永远不进入
        var publicSegmentIds = phase.PublicSegmentIds
            .Where(id => package.Segments.Any(s =>
                s.SegmentId == id && s.Visibility == SegmentVisibility.Public))
            .ToArray();

        // 投票环节只显示本轮自己的汇总：上一轮的票不能让界面以为这一轮已经录过。
        var votes = !phase.IsVotePhase
            ? session.VoteSummary
            : session.VoteHistory.FirstOrDefault(r => r.PhaseId == phase.PhaseId)?.Summary
              ?? (GameEngine.HasSummaryFor(session, package, phase) ? session.VoteSummary : null);

        var unlocked = TruthAccess.IsUnlocked(session);
        var revealed = unlocked
            ? TruthAccess.RevealedTexts(package, session).ToArray()
            : null;

        var projection = new PublicSessionProjection
        {
            SessionId = session.SessionId,
            PublicTitle = package.Title,
            PlayerCount = package.PlayerCount,
            State = session.State.ToString(),
            Revision = session.Revision,
            CurrentPhase = new PublicPhaseView
            {
                PhaseId = phase.PhaseId,
                Title = phase.Title,
                PublicSegmentIds = publicSegmentIds,
                AllowedActions = AllowedActionsFor(session, phase),
                Hint = phase.Hint,
                AdvanceLabel = phase.AdvanceLabel,
                RevealLabel = phase.RevealLabel,
                RequiresAllMaterials = phase.RequiresAllMaterials,
                CanOpenSession = phase.OffersOpeningSpeech,
                Index = phaseIndex + 1,
                Count = package.Phases.Count,
            },
            PhaseTrack = package.Phases
                .Select(p => new PublicPhaseTrackItem { PhaseId = p.PhaseId, Title = p.Title })
                .ToArray(),
            ReleasedMaterialIds = session.ReleasedMaterialIds,
            VoteSummary = votes is null
                ? null
                : new PublicVoteSummaryView
                {
                    Counts = votes.Counts
                        .Select(c => new PublicVoteCount { Option = c.Option, Count = c.Count })
                        .ToArray(),
                    TotalVotes = votes.Total,
                },
            RevealedTexts = revealed,
        };

        // 防御：投影里的段落 ID 不允许出现在私有/真相段集合中
        var secretIds = package.Segments
            .Where(s => s.Visibility != SegmentVisibility.Public)
            .Select(s => s.SegmentId)
            .ToHashSet(StringComparer.Ordinal);
        return projection.EnsureSecretFree(secretIds);
    }

    /// <summary>
    /// 只暴露当前阶段与当前状态真正可用的动作。
    /// </summary>
    public static IReadOnlyList<PublicAction> AllowedActionsFor(Session session, ScriptPhase phase)
    {
        var allowed = new List<PublicAction>();
        switch (session.State)
        {
            case SessionState.Prepared:
                if (phase.AllowedActions.Contains(PublicAction.Start))
                {
                    allowed.Add(PublicAction.Start);
                }

                break;
            case SessionState.Playing:
                foreach (var action in phase.AllowedActions)
                {
                    if (action is PublicAction.Start or PublicAction.Resume)
                    {
                        continue;
                    }

                    allowed.Add(action);
                }

                break;
            case SessionState.Paused:
                allowed.Add(PublicAction.Resume);
                break;
            case SessionState.Revealed:
                allowed.Add(PublicAction.Complete);
                allowed.Add(PublicAction.Pause);
                break;
            case SessionState.Completed:
                break;
        }

        return allowed;
    }
}
