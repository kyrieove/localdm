using LocalDm.Core.Domain;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 主持上下文构造：只使用当前阶段允许公开的原文。
/// 不把完整剧本、私有角色本或真相交给普通文本模型。
/// </summary>
public sealed class PublicContextBuilder
{
    private const int MaxExcerptCharacters = 600;

    /// <summary>
    /// 构造模型可见的公开上下文：只取当前阶段允许公开的段落。
    /// 私有与真相段即使被误传也不会进入上下文。
    /// </summary>
    public string Build(ScriptPackage package, Session session, IReadOnlyList<string> allowedSegmentIds)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);
        if (phase is null)
        {
            return string.Empty;
        }

        var allowed = new HashSet<string>(allowedSegmentIds.Count > 0 ? allowedSegmentIds : phase.PublicSegmentIds, StringComparer.Ordinal);
        var lines = package.Segments
            .Where(s => s.Visibility == SegmentVisibility.Public
                        && allowed.Contains(s.SegmentId)
                        && phase.PublicSegmentIds.Contains(s.SegmentId, StringComparer.Ordinal))
            .Select(s => s.OriginalText)
            .ToArray();

        if (lines.Length == 0)
        {
            return string.Empty;
        }

        var text = string.Join('\n', lines);
        return text.Length > MaxExcerptCharacters ? text[..MaxExcerptCharacters] : text;
    }
}
