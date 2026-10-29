using System.Text;
using LocalDm.Core.Domain;

namespace LocalDm.Core.Hosting;

/// <summary>一条对不上的短句。</summary>
/// <param name="Sentence">备忘里的这条短句（只给诊断用，不上屏）。</param>
/// <param name="Term">这句里照抄卡面、桌上却没念过的那一段。</param>
public sealed record DmMemoOffence(string Sentence, string Term);

/// <summary>
/// 备忘的护栏：只准写对话里出现过的事。
///
/// 整理请求里已经写明「只能写对话里出现过的内容」，人工验收仍然出现过模型把没发放的卡面
/// 细节写进备忘。这里按对话原文逐句核对一遍，拦的是「线索卡里的东西从主持资料漏进备忘」：
/// 备忘里有一句话照着某个线索卡来源原文写了五个字以上、而桌上没人念过这一段，就是没依据的转述。
/// 这是判断，不是内容审查：放行的话原样交出，拦下的话这份备忘整份不采用。
/// </summary>
public static class DmMemoCheck
{
    /// <summary>
    /// 与卡面原文连续相同这么多个字，就当是照卡面转述的。
    ///
    /// 实测门限：五个字的偶然重合会误伤——卡面是「内容只能自己看，可以转述或编造」，
    /// 被切在两个来源块里，而主持人的原话是「可以转述或编造」，两边正好有五个字一样。
    /// 七个字能把这种拼接撞上挡掉，照抄卡面的句子仍然判得到。
    /// </summary>
    public const int VerbatimRunLength = 7;

    /// <summary>对照的依据：旧备忘加上这一轮要整理的原话。</summary>
    public static string BuildSupportingText(DmConversation conversation, int keepFromIndex)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(conversation.Memo))
        {
            builder.Append(conversation.Memo).Append('\n');
        }

        foreach (var turn in conversation.Turns.Take(Math.Clamp(keepFromIndex, 0, conversation.Turns.Count)))
        {
            builder.Append(turn.Content).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// 线索卡原文，逐段给出（卡面正面与卡背都算；组织者手册不算）。
    ///
    /// 卡背是「1楼-书房（现场）」「调查抽屉」这类导航信息，主持人本来就要报出来，
    /// 调用方要按需剔掉，别拿它判主持人念卡。
    /// </summary>
    public static IReadOnlyList<string> CardTexts(ScriptPackage package)
    {
        var materialSpans = package.Materials
            .SelectMany(material => material.SourceSpanIds)
            .ToHashSet(StringComparer.Ordinal);
        return package.Spans
            .Where(span => materialSpans.Contains(span.SpanId))
            .Select(span => span.OriginalText)
            .ToArray();
    }

    /// <summary>
    /// 一段文字里有哪些片段与卡面原文连续相同（每段 7 个字以上）。
    /// 验收用它判「主持人有没有念出卡面」。
    /// </summary>
    public static IReadOnlyList<string> CardFragments(string text, IReadOnlyList<string> cardTexts)
    {
        var fragments = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return fragments;
        }

        foreach (var sentence in Sentences(text))
        {
            var verbatim = LongestSharedRun(sentence, cardTexts);
            if (verbatim.Length >= VerbatimRunLength)
            {
                fragments.Add(verbatim);
            }
        }

        return fragments;
    }

    /// <summary>
    /// 找出备忘里没依据的短句：句中出现线索卡里有、依据里没有的词。
    ///
    /// 只比线索卡（材料分组的来源 span），不比组织者手册：主持人自己的话本来就可以
    /// 带上手册里的流程、规则和房间名，那些不是玩家还没读到的东西。
    /// </summary>
    public static IReadOnlyList<DmMemoOffence> UnsupportedTerms(
        string memo,
        DmConversation conversation,
        ScriptPackage package) =>
        UnsupportedTerms(memo, BuildSupportingText(conversation, conversation.Turns.Count), package);

    /// <summary>同上，依据文本由调用方给。</summary>
    public static IReadOnlyList<DmMemoOffence> UnsupportedTerms(
        string memo,
        string supportingText,
        ScriptPackage package)
    {
        var offences = new List<DmMemoOffence>();
        if (string.IsNullOrWhiteSpace(memo))
        {
            return offences;
        }

        var cardTexts = CardTexts(package);
        if (cardTexts.Count == 0)
        {
            return offences;
        }

        // 主持资料本身就含全部线索卡：一句话只要照着某一段卡面原文写了七个字以上，
        // 而且桌上没人念过这一段，就是转述。逐段比对，不能把全部卡面拼成一段：
        // 换个字就断开，长句子永远判不到。
        foreach (var sentence in Sentences(memo))
        {
            var verbatim = LongestSharedRun(sentence, cardTexts);
            if (verbatim.Length < VerbatimRunLength
                || supportingText.Contains(verbatim, StringComparison.Ordinal))
            {
                continue;
            }

            offences.Add(new DmMemoOffence(sentence, verbatim));
        }

        return offences;
    }

    /// <summary>这句话与卡面原文最长的完全相同片段（逐段比，按位置取出来）。</summary>
    private static string LongestSharedRun(string sentence, IReadOnlyList<string> cardTexts)
    {
        var longest = string.Empty;
        foreach (var text in cardTexts)
        {
            for (var start = 0; start < sentence.Length; start++)
            {
                for (var end = sentence.Length; end - start > longest.Length; end--)
                {
                    if (text.Contains(sentence.AsSpan(start, end - start), StringComparison.Ordinal))
                    {
                        longest = sentence.Substring(start, end - start);
                        break;
                    }
                }
            }
        }

        return longest;
    }

    /// <summary>
    /// 按句末与停顿切句：短句比整句更容易对上卡面原文，
    /// 而卡片原文里的标点常常和主持人的标点不一样，整句比对会漏判。
    /// </summary>
    internal static IEnumerable<string> Sentences(string memo)
    {
        var builder = new StringBuilder();
        foreach (var ch in memo)
        {
            if (ch is '。' or '；' or '！' or '？' or '\n' or '，' or '、' or '：' or '!' or '?' or ';' or ',' or ':')
            {
                var sentence = builder.ToString().Trim();
                if (sentence.Length > 0)
                {
                    yield return sentence;
                }

                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        var last = builder.ToString().Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }
}
