using System.Text;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 一局的 DM 对话记忆：较早对话整理成的备忘 + 之后的原话轮次 + 最近一次真实 token 用量。
///
/// 屏幕上的聊天记录不归这里管；这里只决定发给模型什么。
/// </summary>
public sealed class DmConversation
{
    private readonly List<MagpieMessage> _turns = [];

    /// <summary>本局备忘，没压缩过为 null。</summary>
    public string? Memo { get; private set; }

    /// <summary>备忘之后的原话，只有 user / assistant。</summary>
    public IReadOnlyList<MagpieMessage> Turns => _turns;

    /// <summary>最近一次真实 token 用量。压缩之后清空，下一轮重新估算。</summary>
    public MagpieUsage? LastUsage { get; private set; }

    /// <summary>已经整理过几次。</summary>
    public int CompactionCount { get; private set; }

    /// <summary>记下一句玩家说的话。</summary>
    public void AppendUser(string text) =>
        _turns.Add(new MagpieMessage { Role = "user", Content = text });

    /// <summary>记下一句主持回复，并更新这一轮的真实 token 用量。</summary>
    public void AppendAssistant(string text, MagpieUsage? usage)
    {
        _turns.Add(new MagpieMessage { Role = "assistant", Content = text });
        LastUsage = usage;
    }

    /// <summary>
    /// 用新备忘替换前 keepFromIndex 条原话，只留下较近的对话。
    ///
    /// 压缩之后清空真实用量：它已经对不上剩下的内容，下一轮重新估算，
    /// 直到拿到新的真实用量为止。
    /// </summary>
    public void ApplyCompaction(string memo, int keepFromIndex)
    {
        var keep = Math.Clamp(keepFromIndex, 0, _turns.Count);
        _turns.RemoveRange(0, keep);
        Memo = memo;
        CompactionCount++;
        LastUsage = null;
    }

    /// <summary>
    /// 丢弃最早的若干轮对话（一轮 = 一条 user 加它后面那条 assistant）。
    /// 返回实际丢弃的轮数。
    ///
    /// 这是唯一会丢内容的路径：整理失败而且已经发不出去时才用。
    /// </summary>
    public int DropOldestRounds(int maxRounds)
    {
        var dropped = 0;
        while (dropped < maxRounds && _turns.Count > 0)
        {
            var count = _turns[0].Role == "user" && _turns.Count > 1 && _turns[1].Role == "assistant" ? 2 : 1;
            RemoveFront(count);
            dropped++;
        }

        return dropped;
    }

    /// <summary>存档用：把这一局的记忆拍成一张快照。</summary>
    public DmConversationSnapshot ToSnapshot() => new()
    {
        Memo = Memo,
        Turns = _turns.ToArray(),
        CompactionCount = CompactionCount,
        LastUsage = LastUsage,
    };

    /// <summary>从存档恢复。没有存档就是新的一局。</summary>
    public static DmConversation FromSnapshot(DmConversationSnapshot? snapshot)
    {
        var conversation = new DmConversation();
        if (snapshot is null)
        {
            return conversation;
        }

        conversation._turns.AddRange(snapshot.Turns);
        conversation.Memo = snapshot.Memo;
        conversation.CompactionCount = snapshot.CompactionCount;
        conversation.LastUsage = snapshot.LastUsage;
        return conversation;
    }

    /// <summary>
    /// 删掉最前面的若干条。
    ///
    /// 真实用量就此作废：它量的是丢掉之前的那一版内容，已经对不上剩下的对话。
    /// 清空之后占用退回按文字估算，而文字估算正好就是下一轮请求的实际大小。
    /// </summary>
    private void RemoveFront(int count)
    {
        _turns.RemoveRange(0, count);
        LastUsage = null;
    }
}

/// <summary>
/// 上下文占用的计量与整理时机。
///
/// 不设固定字数上限：按模型的上下文窗口算占用，快满时把较早的对话整理成备忘。
/// </summary>
public static class ContextBudget
{
    /// <summary>给模型回复预留的空间。</summary>
    public const int ReplyReserveTokens = 4_096;

    /// <summary>占用到这个比例，就先整理一轮再发。</summary>
    public const double AutoCompactRatio = 0.80;

    /// <summary>整理失败时，丢弃最早轮次要降到这个比例以下。</summary>
    public const double DropTargetRatio = 0.90;

    /// <summary>保留的最近原话大致占可用窗口的比例。</summary>
    public const double KeepRatio = 0.15;

    /// <summary>至少保留最近 2 轮原话（2 条 user 加对应的 assistant）。</summary>
    public const int MinKeptRounds = 2;

    /// <summary>
    /// 估算一段文字的 token 数：中日韩字符（含全角标点）每个 0.75，其余每个 0.3，向上取整。
    /// 只在没有真实用量时使用。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var wide = 0;
        var narrow = 0;
        foreach (var ch in text)
        {
            if (IsWide(ch))
            {
                wide++;
            }
            else
            {
                narrow++;
            }
        }

        return (int)Math.Ceiling(wide * 0.75 + narrow * 0.3);
    }

    /// <summary>扣掉给回复预留的空间之后，还能放多少内容。</summary>
    public static int UsableTokens(int contextWindowTokens) =>
        Math.Max(1, contextWindowTokens - ReplyReserveTokens);

    /// <summary>
    /// 下一轮请求大概会占多少 token。
    /// 有真实用量时按真实用量加下一条消息算；没有时按文字估算。
    /// </summary>
    public static int EstimateNextPromptTokens(
        DmConversation conversation,
        string systemPrompt,
        string nextUserMessage) =>
        conversation.LastUsage is { } usage
            ? usage.PromptTokens + usage.CompletionTokens + EstimateTokens(nextUserMessage)
            : EstimateTextTokens(conversation, systemPrompt, nextUserMessage);

    /// <summary>
    /// 不看真实用量、只按文字估算下一轮的占用。
    /// 丢掉原话之后真实用量已经对不上内容，这时按文字算更准。
    /// </summary>
    public static int EstimateTextTokens(
        DmConversation conversation,
        string systemPrompt,
        string nextUserMessage)
    {
        var builder = new StringBuilder();
        builder.Append(systemPrompt);
        if (!string.IsNullOrEmpty(conversation.Memo))
        {
            builder.Append(conversation.Memo);
        }

        foreach (var turn in conversation.Turns)
        {
            builder.Append(turn.Content);
        }

        builder.Append(nextUserMessage);
        return EstimateTokens(builder.ToString());
    }

    /// <summary>上下文占用比例，界面用它显示百分比。</summary>
    public static double UsageRatio(
        DmConversation conversation,
        string systemPrompt,
        string nextUserMessage,
        int contextWindowTokens) =>
        EstimateNextPromptTokens(conversation, systemPrompt, nextUserMessage)
        / (double)UsableTokens(contextWindowTokens);

    /// <summary>
    /// 保留部分的起点：更早的对话交给整理。
    ///
    /// 从最新往前累加，累加到可用窗口的一成半就停；
    /// 但至少保留最近 2 轮（2 条 user 及其后的消息）。
    /// 起点落在 assistant 上时，向前移到最近的一条 user。
    /// </summary>
    public static int KeepFromIndex(DmConversation conversation, int contextWindowTokens) =>
        SelectKeepStart(conversation.Turns, contextWindowTokens, MinKeptRounds);

    /// <summary>
    /// 手动整理的保留起点：只留最近 2 轮，其余全部整理。
    /// 起点落在 assistant 上时向前移到最近的一条 user。
    /// </summary>
    public static int ManualKeepFromIndex(DmConversation conversation) =>
        SelectKeepStart(conversation.Turns, contextWindowTokens: null, MinKeptRounds);

    /// <summary>
    /// 选出保留部分的起点，规则只写这一处：
    ///
    /// 先按「至少保留最近 minRounds 轮」定出最晚起点，
    /// 给了窗口就再往前累加到可用窗口的一成半；
    /// 最后把起点挪到最近的 user 上。
    ///
    /// 两条保证因此不会互相打架：预算吃得下就多留，
    /// 一条超长的回复把预算一口气用完时，也仍然留下最近 2 轮。
    /// </summary>
    private static int SelectKeepStart(
        IReadOnlyList<MagpieMessage> turns,
        int? contextWindowTokens,
        int minRounds)
    {
        if (turns.Count == 0)
        {
            return 0;
        }

        // 至少保留最近 minRounds 轮：起点就是最近那条 user 往前数第 minRounds 条 user。
        var roundStart = RoundStart(turns, Math.Max(1, minRounds));
        if (contextWindowTokens is not { } window)
        {
            return AlignToUser(turns, roundStart);
        }

        var budget = (int)(UsableTokens(window) * KeepRatio);
        var tokens = 0;
        var index = roundStart;

        // 预算吃得下就再往前留；一条超长的回复把预算一次用完时，也不会少留这 2 轮。
        for (var i = roundStart - 1; i >= 0; i--)
        {
            tokens += EstimateTokens(turns[i].Content);
            index = i;
            if (tokens >= budget)
            {
                break;
            }
        }

        return AlignToUser(turns, index);
    }

    /// <summary>
    /// 最近 minRounds 轮的起点：从末尾往前数第 minRounds 条 user 的下标。
    /// 轮数不够时返回 0。
    /// </summary>
    private static int RoundStart(IReadOnlyList<MagpieMessage> turns, int minRounds)
    {
        var remaining = minRounds;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].Role != "user")
            {
                continue;
            }

            remaining--;
            if (remaining == 0)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// 把起点挪到最近的 user 上。
    ///
    /// 保留部分的第一条必须是 user：模型看到的第一句话应当是玩家说的。
    /// 只能往前挪（保留更多），往后挪会让「至少保留 2 轮」落空，甚至一条都不剩。
    /// </summary>
    private static int AlignToUser(IReadOnlyList<MagpieMessage> turns, int index)
    {
        var aligned = Math.Clamp(index, 0, turns.Count);
        while (aligned > 0 && turns[aligned].Role != "user")
        {
            aligned--;
        }

        return aligned;
    }

    /// <summary>中日韩字符与全角标点：按 0.75 个 token 记。</summary>
    private static bool IsWide(char ch) =>
        (ch >= '一' && ch <= '鿿')   // CJK 统一表意文字
        || (ch >= '㐀' && ch <= '䶿') // 扩展 A
        || (ch >= '　' && ch <= '〿') // 中日韩符号与标点
        || (ch >= '＀' && ch <= '￯'); // 全角字符
}

