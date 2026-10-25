using System.Text.RegularExpressions;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 把长文切成朗读段：Edge 每段单独合成，超时按段算；某段失败时从这段接着读，不从头。
/// </summary>
public static class SpeechChunks
{
    public const int MaxChars = 500;

    private static readonly Regex SentenceEnd = new("(?<=[。！？!?；;])", RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string text, int maxChars = MaxChars)
    {
        // 先拆成不超过 maxChars 的单元：段落；太长的段落按句末；还太长的句子硬切。
        var units = new List<string>();
        foreach (var paragraph in text.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (paragraph.Length <= maxChars)
            {
                units.Add(paragraph);
                continue;
            }

            foreach (var sentence in SentenceEnd.Split(paragraph).Where(s => s.Length > 0))
            {
                for (var start = 0; start < sentence.Length; start += maxChars)
                {
                    units.Add(sentence.Substring(start, Math.Min(maxChars, sentence.Length - start)));
                }
            }
        }

        // 再把相邻单元合并到不超过 maxChars，减少 Edge 进程启动次数。
        var chunks = new List<string>();
        var current = "";
        foreach (var unit in units)
        {
            if (current.Length == 0)
            {
                current = unit;
            }
            else if (current.Length + 1 + unit.Length <= maxChars)
            {
                current += "\n" + unit;
            }
            else
            {
                chunks.Add(current);
                current = unit;
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }
}
