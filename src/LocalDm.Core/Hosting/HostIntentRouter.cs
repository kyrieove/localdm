using LocalDm.Core.Domain;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 把主持台文字分成普通公共请求和状态意图提案。
/// 状态意图只能产生待确认提案，绝不调用 CommandDispatcher、不写状态。
/// </summary>
public sealed class HostIntentRouter
{
    private static readonly string[] AdvanceKeywords =
    [
        "下一环节", "下一步", "下一个阶段", "推进", "开始下一轮", "进入下一阶段",
    ];

    private static readonly string[] RevealKeywords =
    [
        "揭秘", "公布真相", "揭露真相",
    ];

    private static readonly string[] SecretProbeKeywords =
    [
        "真相是什么", "谁是凶手", "告诉我凶手", "秘密是什么", "给我看真相", "剧透",
    ];

    /// <summary>整句输入就等于这条命令，不夹带别的意思。</summary>
    private static readonly string[] CompactKeywords =
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

        if (IsWholeSentence(trimmed, CompactKeywords))
        {
            return new HostIntent(HostIntentKind.Compact, null);
        }

        if (ContainsAny(trimmed, SecretProbeKeywords))
        {
            return new HostIntent(HostIntentKind.PublicQuestion, null);
        }

        if (ContainsAny(trimmed, RevealKeywords))
        {
            return new HostIntent(HostIntentKind.ProposeCommand, PublicAction.Reveal);
        }

        if (ContainsAny(trimmed, AdvanceKeywords))
        {
            return new HostIntent(HostIntentKind.ProposeCommand, PublicAction.Advance);
        }

        return new HostIntent(HostIntentKind.PublicQuestion, null);
    }

    /// <summary>
    /// 整句匹配：去掉首尾空白与末尾标点之后，整句等于命令词才算这条命令。
    /// 「帮我压缩上下文吧」不是命令，「压缩上下文」才是。
    /// </summary>
    private static bool IsWholeSentence(string text, IEnumerable<string> commands) =>
        commands.Contains(StripTrailingPunctuation(text), StringComparer.Ordinal);

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

    private static bool ContainsAny(string text, IEnumerable<string> keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.Ordinal));
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
