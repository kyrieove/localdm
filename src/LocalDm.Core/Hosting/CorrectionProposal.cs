namespace LocalDm.Core.Hosting;

/// <summary>模型能提议的改正。提议只到确认条，执行要主持人点（ADR 0010）。</summary>
public enum CorrectionKind { RevertPhase, CorrectVote }

/// <summary>
/// 从模型回复里取出改正提议：只认单独一行、整行等于标记的写法，句子中间出现不算。
/// 取出后这一行从文字里去掉，不上屏、不进记忆、不朗读。
/// </summary>
public static class CorrectionProposal
{
    public const string RevertTag = "〔提议：回到上一环节〕";
    public const string CorrectVoteTag = "〔提议：重新录入票数〕";

    public static (string Text, CorrectionKind? Kind) Extract(string reply)
    {
        CorrectionKind? kind = null;
        var lines = new List<string>();
        foreach (var line in reply.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim() == RevertTag)
            {
                kind = CorrectionKind.RevertPhase;
            }
            else if (line.Trim() == CorrectVoteTag)
            {
                kind = CorrectionKind.CorrectVote;
            }
            else
            {
                lines.Add(line);
            }
        }

        return kind is null ? (reply, null) : (string.Join("\n", lines).Trim(), kind);
    }
}
