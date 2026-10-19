using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 主持意图：每个界面按钮对应一条意图，不是一句写死的主持词。
/// 按钮只声明「现在要做什么」，具体措辞由模型生成，再由公开政策核对。
/// </summary>
public enum HostNarrationIntent
{
    /// <summary>开场：请玩家开始阅读角色本。</summary>
    OpenSession = 0,

    /// <summary>宣布进入某个环节并说明要做什么。</summary>
    AnnouncePhase = 1,

    /// <summary>提示领取线索 / 材料编号。</summary>
    ReleaseMaterials = 2,

    /// <summary>主持纸笔投票并说明由主持人汇总。</summary>
    CallVote = 3,

    /// <summary>公开真相。</summary>
    RevealTruth = 4,

    /// <summary>进入复盘。</summary>
    EnterReview = 5,

    /// <summary>暂停保存。</summary>
    PauseSession = 6,
}

/// <summary>
/// 把意图 + 当前公开上下文变成给模型的提示词，并给出核对用的公开依据。
/// 私有段与真相段永远不会进入这里。
/// </summary>
public static class HostNarration
{
    private const string SystemPrompt =
        "你是在一台电脑上为桌边玩家主持剧本杀的主持人。只说中文，两到四句话。\n" +
        "规则：\n" +
        "1. 只能依据我给出的公开内容说话，不得补充、猜测或编造剧情细节；\n" +
        "2. 不得透露角色秘密、凶手或未公开真相；\n" +
        "3. 不得自行推进流程或宣布规则之外的事；\n" +
        "4. 语气自然，像主持人在桌边对大家说话，不要复述指令。";

    /// <summary>该意图对应的主持要点，写进提示词。</summary>
    public static string IntentLine(HostNarrationIntent intent, string phaseTitle) => intent switch
    {
        HostNarrationIntent.OpenSession => "主持开场，请全体玩家开始阅读自己的角色本。",
        HostNarrationIntent.AnnouncePhase => $"宣布现在进入「{phaseTitle}」环节，并说明这一步大家要做什么。",
        HostNarrationIntent.ReleaseMaterials => "提示玩家按编号领取线索，并说明领取后自行阅读。",
        HostNarrationIntent.CallVote => "主持纸笔投票，说明由主持人统一汇总录入。",
        HostNarrationIntent.RevealTruth => "宣布现在公开真相，按已给出的公开原文说明。",
        HostNarrationIntent.EnterReview => "进入复盘，请大家一起回顾这一局。",
        HostNarrationIntent.PauseSession => "说明这一局已暂停保存，稍后可以继续。",
        _ => "按当前环节主持。",
    };

    /// <summary>
    /// 构造给模型的消息。公开依据 = 环节名 + 原本条件 + 当前阶段允许公开的原文。
    /// 条件本身是无剧透的，可以用于措辞依据。
    /// </summary>
    public static IReadOnlyList<MagpieMessage> BuildMessages(
        ScriptPackage package,
        Session session,
        HostNarrationIntent intent)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);

        var publicLines = phase is null
            ? Array.Empty<string>()
            : package.Segments
                .Where(s => s.Visibility == SegmentVisibility.Public
                            && phase.PublicSegmentIds.Contains(s.SegmentId, StringComparer.Ordinal))
                .Select(s => s.OriginalText)
                .ToArray();

        var conditions = phase?.Conditions ?? [];

        var evidence = string.Join("\n", new[]
            {
                $"当前环节：{phase?.Title ?? "未开始"}",
                conditions.Count > 0 ? "本环节条件：\n- " + string.Join("\n- ", conditions) : string.Empty,
                publicLines.Length > 0 ? "可公开的原文：\n" + string.Join("\n", publicLines) : string.Empty,
            }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var user =
            $"【这次要做的事】{IntentLine(intent, phase?.Title ?? "开场")}\n\n" +
            $"【可以依据的公开内容】\n{evidence}\n\n" +
            "请给出你要对桌边玩家说的主持词。";

        return
        [
            new MagpieMessage { Role = "system", Content = SystemPrompt },
            new MagpieMessage { Role = "user", Content = user },
        ];
    }

    /// <summary>
    /// 实时互动：玩家/主持人的原话进 user 消息，公开上下文一并给出。
    /// 可以问规则、问当前环节、问怎么投票；秘密不在上下文里，模型无从泄露。
    /// </summary>
    public static IReadOnlyList<MagpieMessage> BuildAskMessages(
        ScriptPackage package,
        Session session,
        string userText)
    {
        var evidence = BuildGroundingText(package, session);
        var user =
            $"【玩家原话】{userText}\n\n" +
            $"【当前可依据的公开内容】\n{evidence}\n\n" +
            "请像桌边主持人一样回应。若问到未公开的信息，说明现在还不能说，不要编造。";

        return
        [
            new MagpieMessage { Role = "system", Content = SystemPrompt },
            new MagpieMessage { Role = "user", Content = user },
        ];
    }

    /// <summary>
    /// 当前可公开的依据文本（不含私有段、不含真相段）。
    /// </summary>
    public static string BuildGroundingText(ScriptPackage package, Session session)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);
        if (phase is null)
        {
            return string.Empty;
        }

        var publicLines = package.Segments
            .Where(s => s.Visibility == SegmentVisibility.Public
                        && phase.PublicSegmentIds.Contains(s.SegmentId, StringComparer.Ordinal))
            .Select(s => s.OriginalText);

        return string.Join('\n', new[] { phase.Title }
            .Concat(phase.Conditions)
            .Concat(publicLines));
    }
}
