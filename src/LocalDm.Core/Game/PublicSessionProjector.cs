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

        // 只有 Public 段允许出现在公共投影里；Private / Truth 永远不进入
        var publicSegmentIds = phase.PublicSegmentIds
            .Where(id => package.Segments.Any(s =>
                s.SegmentId == id && s.Visibility == SegmentVisibility.Public))
            .ToArray();

        var unlocked = session.State is SessionState.Revealed or SessionState.Completed;
        var revealed = unlocked
            ? package.Truth.RevealedSegmentIds
                .Select(id => package.Segments.FirstOrDefault(s => s.SegmentId == id))
                .Where(s => s is not null && s.Visibility == SegmentVisibility.Truth)
                .Select(s => s!.OriginalText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray()
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
            },
            ReleasedMaterialIds = session.ReleasedMaterialIds,
            VoteSummary = session.VoteSummary is null
                ? null
                : new PublicVoteSummaryView
                {
                    Counts = session.VoteSummary.Counts
                        .Select(c => new PublicVoteCount { Option = c.Option, Count = c.Count })
                        .ToArray(),
                    TotalVotes = session.VoteSummary.Total,
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
