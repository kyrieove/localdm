using LocalDm.Core.Hosting;

namespace LocalDm.Core.Import;

/// <summary>一条被判为可能剧透的条目：环节序号、字段、以及结论。原文不外带，避免界面漏出去。</summary>
public sealed record FlowSpoilerHit(int StepIndex, string Field, string Reason)
{
    /// <summary>给导入的人看的一句话：不显示原文，只说要重写。</summary>
    public string Display => "这条提示可能含剧情，已隐藏，请重写";
}

/// <summary>
/// 剧透检查：起草出来的 title / hint / conditions 与真相页原文连续相同 7 个字以上，
/// 就判为可能剧透，界面改成「已隐藏，请重写」，重写或删掉之前不能继续。
///
/// 只拦「照抄真相」，拦不住改写过的剧情 —— 所以 ④ 页顶部还要常驻一行提示，
/// 让导入的人自己删掉读着像剧情的那几条。
/// 7 字门限与 <see cref="DmMemoCheck"/> 共用同一个常量，两处不再各自定值。
/// </summary>
public static class FlowSpoilerCheck
{
    /// <summary>与真相页原文连续相同这么多个字，就判为照抄。</summary>
    public const int VerbatimRunLength = DmMemoCheck.VerbatimRunLength;

    /// <summary>
    /// 逐条检查。返回空数组表示没发现照抄。
    /// </summary>
    /// <param name="flow">起草或手改后的流程。</param>
    /// <param name="truthPageTexts">真相页原文（只用来比对，不显示、不外带）。</param>
    public static IReadOnlyList<FlowSpoilerHit> Check(
        Domain.ScriptFlow flow,
        IReadOnlyList<string> truthPageTexts)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(truthPageTexts);

        var hits = new List<FlowSpoilerHit>();
        if (truthPageTexts.Count == 0)
        {
            return hits;
        }

        for (var index = 0; index < flow.Steps.Count; index++)
        {
            var step = flow.Steps[index];
            CheckOne(index, "标题", step.Title, truthPageTexts, hits);
            CheckOne(index, "提示", step.Hint, truthPageTexts, hits);
            for (var condition = 0; condition < step.Conditions.Count; condition++)
            {
                CheckOne(index, $"条件 {condition + 1}", step.Conditions[condition], truthPageTexts, hits);
            }
        }

        return hits;
    }

    /// <summary>一段文字是否照抄了任何一页真相。</summary>
    public static bool IsCopy(string? text, IReadOnlyList<string> truthPageTexts)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 逐句比对：卡片与手册的标点常常和主持人的写法不一样，整段比对会漏掉。
        foreach (var sentence in Sentences(text))
        {
            if (truthPageTexts.Any(page => SharedRun(sentence, page) >= VerbatimRunLength))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckOne(
        int stepIndex,
        string field,
        string? text,
        IReadOnlyList<string> truthPageTexts,
        List<FlowSpoilerHit> hits)
    {
        if (IsCopy(text, truthPageTexts))
        {
            hits.Add(new FlowSpoilerHit(stepIndex, field, "与真相页连续相同 7 个字以上"));
        }
    }

    /// <summary>这句话与某一页真相最长的完全相同片段有多长。</summary>
    private static int SharedRun(string sentence, string pageText)
    {
        var longest = 0;
        for (var start = 0; start < sentence.Length; start++)
        {
            for (var end = sentence.Length; end - start > longest; end--)
            {
                if (pageText.Contains(sentence.AsSpan(start, end - start), StringComparison.Ordinal))
                {
                    longest = end - start;
                    break;
                }
            }
        }

        return longest;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in text)
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
