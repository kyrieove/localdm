using LocalDm.Core.Domain;

namespace LocalDm.Core.Game;

/// <summary>
/// 规则核心：阶段迁移、材料释放、票数汇总与揭秘。
/// 模型没有状态写权限；所有外部输入先验证再交这里。
/// </summary>
public static class GameEngine
{
    /// <summary>
    /// 计算一次命令的新会话状态。不合法时抛 DomainRuleException，且调用方不得产生事件。
    /// </summary>
    public static Session Apply(Session current, ScriptPackage package, GameCommand command)
    {
        command.Validate();
        current.ValidateBindings(package.PackageId, package.Version, package.ManifestHash);

        if (current.Revision != command.ExpectedRevision)
        {
            throw new DomainRuleException(DomainErrorCode.STALE_REVISION, "当前 revision 已变化，请重新读取阶段。");
        }

        var next = command.Type switch
        {
            GameCommandType.Start => Start(current, package, command),
            GameCommandType.Advance => Advance(current, package, command),
            GameCommandType.ReleaseMaterials => ReleaseMaterials(current, package, command),
            GameCommandType.SubmitVoteSummary => SubmitVoteSummary(current, package, command),
            GameCommandType.Reveal => Reveal(current, package, command),
            GameCommandType.Pause => Pause(current),
            GameCommandType.Resume => Resume(current, package),
            GameCommandType.Complete => Complete(current, package),
            _ => throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "未知命令类型。"),
        };

        // revision 从 0 开始，每次提交恰加 1
        return next with { Revision = current.Revision + 1 };
    }

    private static Session Start(Session current, ScriptPackage package, GameCommand command)
    {
        if (package.Validation != PackageValidationState.Ready)
        {
            throw new DomainRuleException(DomainErrorCode.NOT_READY, "剧本包尚未 Ready，不能开局。");
        }

        if (current.State != SessionState.Prepared)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "只有准备完成的一局可以开始。");
        }

        if (!command.GroupReady)
        {
            throw new DomainRuleException(DomainErrorCode.GROUP_NOT_READY, "请先确认大家已口头准备好。");
        }

        var first = package.Phases.FirstOrDefault()
                    ?? throw new DomainRuleException(DomainErrorCode.NOT_READY, "剧本包没有阶段。");
        return current with
        {
            State = SessionState.Playing,
            CurrentPhaseId = first.PhaseId,
            ResumeState = null,
        };
    }

    private static Session Advance(Session current, ScriptPackage package, GameCommand command)
    {
        if (current.State != SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在游戏中，不能推进。");
        }

        if (!command.GroupReady)
        {
            throw new DomainRuleException(DomainErrorCode.GROUP_NOT_READY, "请先确认大家已口头准备好。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.AllowedActions.Contains(PublicAction.Advance))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "原本条件不允许在此阶段推进。");
        }

        // 投票阶段未提交合法汇总时不能推进
        if (phase.IsVotePhase && current.VoteSummary is null)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "投票阶段需要先提交有效的票数汇总。");
        }

        var index = IndexOfPhase(package, phase.PhaseId);
        if (index < 0 || index + 1 >= package.Phases.Count)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "已经是最后一个阶段。");
        }

        var next = package.Phases[index + 1];
        var released = current.ReleasedMaterialIds;
        if (next.MaterialIds.Count > 0)
        {
            released = [.. released.Union(next.MaterialIds, StringComparer.Ordinal)];
        }

        return current with
        {
            CurrentPhaseId = next.PhaseId,
            ReleasedMaterialIds = released,
        };
    }

    private static Session ReleaseMaterials(Session current, ScriptPackage package, GameCommand command)
    {
        if (current.State != SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在游戏中。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.AllowedActions.Contains(PublicAction.ReleaseMaterials))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "本阶段原本不允许发放材料。");
        }

        if (command.MaterialIds.Count == 0)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "未指定材料编号。");
        }

        foreach (var id in command.MaterialIds)
        {
            if (!phase.MaterialIds.Contains(id, StringComparer.Ordinal))
            {
                throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "材料编号不属于当前阶段。");
            }
        }

        return current with
        {
            ReleasedMaterialIds = [.. current.ReleasedMaterialIds.Union(command.MaterialIds, StringComparer.Ordinal)],
        };
    }

    private static Session SubmitVoteSummary(Session current, ScriptPackage package, GameCommand command)
    {
        if (current.State != SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在游戏中。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.IsVotePhase)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "本阶段原本不包含投票。");
        }

        if (!command.Confirmed)
        {
            throw new DomainRuleException(DomainErrorCode.CONFIRMATION_REQUIRED, "提交票数需要二次确认。");
        }

        var votes = (command.Votes ?? throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "缺少票数。"))
            .Validate(package.PlayerCount);

        return current with { VoteSummary = votes };
    }

    private static Session Reveal(Session current, ScriptPackage package, GameCommand command)
    {
        if (current.State is not SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "只有游戏进行中可以揭秘。");
        }

        if (!command.Confirmed)
        {
            throw new DomainRuleException(DomainErrorCode.CONFIRMATION_REQUIRED, "揭秘需要明确确认。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.IsRevealPhase)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "本阶段原本不允许揭秘。");
        }

        // 原本要求投票的剧本必须先有合法汇总
        var votePhaseExists = package.Phases.Any(p => p.IsVotePhase);
        if (votePhaseExists && current.VoteSummary is null)
        {
            throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "尚未提交投票结果，不能揭秘。");
        }

        return current with { State = SessionState.Revealed, ResumeState = null };
    }

    private static Session Pause(Session current)
    {
        if (current.State is not (SessionState.Playing or SessionState.Revealed))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前状态不能暂停。");
        }

        return current with { State = SessionState.Paused, ResumeState = current.State };
    }

    private static Session Resume(Session current, ScriptPackage package)
    {
        if (current.State != SessionState.Paused)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在暂停状态。");
        }

        current.ValidateBindings(package.PackageId, package.Version, package.ManifestHash);

        // 恢复先前的状态（Playing 或 Revealed），不能覆盖揭秘状态
        return current with
        {
            State = current.ResumeState ?? SessionState.Playing,
            ResumeState = null,
        };
    }

    private static Session Complete(Session current, ScriptPackage package)
    {
        if (current.State == SessionState.Paused && current.ResumeState == SessionState.Revealed)
        {
            // 已揭秘后暂停：允许直接复盘
            return current with { State = SessionState.Completed, ResumeState = null };
        }

        if (current.State != SessionState.Revealed)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "需要先揭秘才能复盘。");
        }

        return current with { State = SessionState.Completed, ResumeState = null };
    }

    private static ScriptPhase CurrentPhase(Session current, ScriptPackage package)
    {
        if (string.IsNullOrWhiteSpace(current.CurrentPhaseId))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前没有阶段。");
        }

        return package.Phases.FirstOrDefault(p => p.PhaseId == current.CurrentPhaseId)
               ?? throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "当前阶段不属于该剧本包。");
    }

    private static int IndexOfPhase(ScriptPackage package, string phaseId)
    {
        for (var i = 0; i < package.Phases.Count; i++)
        {
            if (package.Phases[i].PhaseId == phaseId)
            {
                return i;
            }
        }

        return -1;
    }
}
