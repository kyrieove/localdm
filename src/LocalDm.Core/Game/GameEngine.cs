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
            GameCommandType.Start => Start(current, package),
            GameCommandType.Advance => Advance(current, package),
            GameCommandType.ReleaseMaterials => ReleaseMaterials(current, package, command),
            GameCommandType.SubmitVoteSummary => SubmitVoteSummary(current, package, command),
            GameCommandType.Reveal => Reveal(current, package),
            GameCommandType.Pause => Pause(current),
            GameCommandType.Resume => Resume(current, package),
            GameCommandType.Complete => Complete(current, package),
            _ => throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "未知命令类型。"),
        };

        // revision 从 0 开始，每次提交恰加 1
        return next with { Revision = current.Revision + 1 };
    }

    private static Session Start(Session current, ScriptPackage package)
    {
        if (package.Validation != PackageValidationState.Ready)
        {
            throw new DomainRuleException(DomainErrorCode.NOT_READY, "剧本包尚未 Ready，不能开局。");
        }

        if (current.State != SessionState.Prepared)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "只有准备完成的一局可以开始。");
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

    private static Session Advance(Session current, ScriptPackage package)
    {
        if (current.State != SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "当前不在游戏中，不能推进。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.AllowedActions.Contains(PublicAction.Advance))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "原本条件不允许在此阶段推进。");
        }

        // 投票环节必须提交「属于这一轮」的汇总：上一轮的汇总不能带过来。
        if (phase.IsVotePhase && !HasSummaryFor(current, package, phase))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "这一轮投票还没有提交汇总。");
        }

        var index = IndexOfPhase(package, phase.PhaseId);
        if (index < 0 || index + 1 >= package.Phases.Count)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "已经是最后一个阶段。");
        }

        var next = package.Phases[index + 1];
        var released = current.ReleasedMaterialIds;

        // 允许手动发放的阶段（真实剧本的搜证阶段）：材料不在「进入阶段时」整批并入，
        // 否则主持人一进搜证就看到全部牌堆已发放，逐个发牌的按钮全程灰显。
        // 改为由主持人在本阶段内按需发放。
        if (phase.AllowedActions.Contains(PublicAction.ReleaseMaterials))
        {
            var pending = phase.MaterialIds
                .Where(id => !released.Contains(id, StringComparer.Ordinal))
                .ToArray();

            // 剧本声明「本轮材料必须发完才能继续」时，这是机器条件：
            // 少发一份就拒绝推进，而不是替主持人补发。
            if (phase.RequiresAllMaterials && pending.Length > 0)
            {
                throw new DomainRuleException(
                    DomainErrorCode.PHASE_CONDITION_UNMET,
                    $"本环节还有 {pending.Length} 份材料没有发放。");
            }

            // 但离开本阶段时仍要把没发完的编号补齐：主持人若直接推进跳过本阶段，
            // 这些线索不允许永久丢失（否则该阶段之后再也无处可发）。
            if (pending.Length > 0)
            {
                released = [.. released.Union(pending, StringComparer.Ordinal)];
            }
        }

        // 进入下一阶段：若下一阶段不允许手动发放（如被动随阶段公开的材料包），
        // 则在进入时自动释放。
        if (next.MaterialIds.Count > 0 && !next.AllowedActions.Contains(PublicAction.ReleaseMaterials))
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

        var votes = (command.Votes ?? throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "缺少票数。"))
            .Validate(package.PlayerCount);

        // 汇总绑定到这一轮：下一轮投票必须重新提交，历史按顺序留着。
        return current with
        {
            VoteSummary = votes,
            VoteSummaryPhaseId = phase.PhaseId,
            VoteHistory =
            [
                .. current.VoteHistory,
                new PhaseVoteRecord { PhaseId = phase.PhaseId, Summary = votes },
            ],
        };
    }

    /// <summary>
    /// 这个投票环节是否已经有属于它自己的合法汇总。
    ///
    /// 判据是历史里有没有这一轮的记录 —— 只看「最新汇总」会把第二轮顶掉第一轮，
    /// 于是揭秘时第一轮看起来又没投过。
    /// 旧存档没有历史（当时只支持单个投票环节）：那种剧本按「属于本轮」处理，不会卡死。
    /// </summary>
    private static bool HasSummaryFor(Session session, ScriptPackage package, ScriptPhase phase)
    {
        if (session.VoteHistory.Any(r => string.Equals(r.PhaseId, phase.PhaseId, StringComparison.Ordinal)))
        {
            return true;
        }

        if (session.VoteSummary is null)
        {
            return false;
        }

        if (session.VoteSummaryPhaseId is not null)
        {
            return string.Equals(session.VoteSummaryPhaseId, phase.PhaseId, StringComparison.Ordinal);
        }

        return package.Phases.Count(p => p.IsVotePhase) == 1;
    }

    private static Session Reveal(Session current, ScriptPackage package)
    {
        if (current.State is not SessionState.Playing)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "只有游戏进行中可以揭秘。");
        }

        var phase = CurrentPhase(current, package);
        if (!phase.IsRevealPhase)
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "本阶段原本不允许揭秘。");
        }

        // 已经过去的每一轮投票都必须有自己的合法汇总，不能拿第一轮的票数顶第二轮。
        var revealIndex = IndexOfPhase(package, phase.PhaseId);
        for (var i = 0; i < revealIndex; i++)
        {
            var earlier = package.Phases[i];
            if (earlier.IsVotePhase && !HasSummaryFor(current, package, earlier))
            {
                throw new DomainRuleException(DomainErrorCode.VOTE_INVALID, "还有一轮投票没有合法汇总，不能揭秘。");
            }
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
        if (current.State != SessionState.Revealed
            && !(current.State == SessionState.Paused && current.ResumeState == SessionState.Revealed))
        {
            throw new DomainRuleException(DomainErrorCode.PHASE_CONDITION_UNMET, "需要先揭秘才能复盘。");
        }

        var nextPhaseId = current.CurrentPhaseId;
        if (!string.IsNullOrEmpty(current.CurrentPhaseId))
        {
            var index = IndexOfPhase(package, current.CurrentPhaseId);
            // 只在「下一阶段就是剧本的最后一个阶段」时才前进。
            // 复盘是终态，不能指向一个仍可继续推进的中间阶段（否则投影会按那个阶段
            // 的标题与公开段渲染，公开内容与真相错位）。剧本没有复盘阶段时保持原阶段。
            if (index >= 0 && index + 2 == package.Phases.Count)
            {
                nextPhaseId = package.Phases[index + 1].PhaseId;
            }
        }

        return current with
        {
            State = SessionState.Completed,
            CurrentPhaseId = nextPhaseId,
            ResumeState = null
        };
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
