using LocalDm.Core.Domain;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 把主持台文字分成普通公共请求和状态意图提案。
/// 状态意图只能产生待确认提案，绝不调用 CommandDispatcher、不写状态。
/// </summary>
public sealed class HostIntentRouter
{
    /// <summary>整句说出来才是推进命令。</summary>
    private static readonly string[] AdvanceCommands =
    [
        "下一环节", "进入下一环节", "下一阶段", "进入下一阶段", "开始下一轮", "推进",
    ];

    /// <summary>整句说出来才是揭秘命令。</summary>
    private static readonly string[] RevealCommands =
    [
        "揭秘", "公布真相", "揭露真相", "公开真相",
    ];

    /// <summary>整句输入就等于这条命令，不夹带别的意思。</summary>
    private static readonly string[] CompactCommands =
    [
        "/compact", "压缩上下文",
    ];

    public HostIntent Route(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new HostIntent(HostIntentKind.PublicQuestion, null);
        }

        // 整句匹配：「下一步我该做什么？」是在问，不是在下令。
        var sentence = StripTrailingPunctuation(trimmed);

        if (IsWholeSentence(sentence, CompactCommands))
        {
            return new HostIntent(HostIntentKind.Compact, null);
        }

        if (IsWholeSentence(sentence, RevealCommands))
        {
            return new HostIntent(HostIntentKind.ProposeCommand, PublicAction.Reveal);
        }

        if (IsWholeSentence(sentence, AdvanceCommands))
        {
            return new HostIntent(HostIntentKind.ProposeCommand, PublicAction.Advance);
        }

        // 其余一律交给模型：「谁是凶手？」这类问题由主持提示词里的准则回应。
        return new HostIntent(HostIntentKind.PublicQuestion, null);
    }

    /// <summary>
    /// 整句匹配：去掉首尾空白与末尾标点之后，整句等于命令词才算这条命令。
    /// 「帮我压缩上下文吧」不是命令，「压缩上下文」才是。
    /// </summary>
    private static bool IsWholeSentence(string text, IEnumerable<string> commands) =>
        commands.Contains(text, StringComparer.Ordinal);

    /// <summary>去掉末尾的标点与空白。</summary>
    private static string StripTrailingPunctuation(string text)
    {
        var trimmed = text.Trim();
        while (trimmed.Length > 0 && "。！？!?，, ".Contains(trimmed[^1]))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }
}

public enum HostIntentKind
{
    /// <summary>普通公共请求：走 PublicHostRequest。</summary>
    PublicQuestion = 0,

    /// <summary>状态意图：只打开确认视图，不提交。</summary>
    ProposeCommand = 1,

    /// <summary>整理上下文：等同点「压缩上下文」按钮，不进入对话记忆。</summary>
    Compact = 2,
}

public sealed record HostIntent(HostIntentKind Kind, PublicAction? ProposedAction)
{
    public static HostIntent Public() => new(HostIntentKind.PublicQuestion, null);
}
