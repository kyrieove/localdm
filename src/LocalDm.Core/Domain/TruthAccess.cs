namespace LocalDm.Core.Domain;

/// <summary>
/// 「真相是否已解锁」的唯一判定处。
///
/// 秘密隔离是这个产品的核心承诺，而「已解锁」这个状态在引擎、投影、就绪检查与
/// 主持口播里各被判断过一次。判定一旦分叉，就会出现「引擎认为已揭秘、投影却仍
/// 锁着真相」或「暂停后真相从口播依据里消失」这类难查的漂移，因此只在这里定义一次。
/// </summary>
public static class TruthAccess
{
    /// <summary>
    /// 真相已解锁 = 状态为 Revealed / Completed，
    /// 或在已揭秘的状态下暂停（State = Paused 且 ResumeState = Revealed）。
    ///
    /// 暂停必须算作已解锁：引擎的 Complete 明确把「暂停且 ResumeState = Revealed」
    /// 视为已揭秘（可直接复盘），若这里不认，主持人一按暂停，公开口播与普通问答
    /// 的依据就会突然失去真相文本。
    /// </summary>
    public static bool IsUnlocked(Session session) =>
        session.State is SessionState.Revealed or SessionState.Completed
        || (session.State == SessionState.Paused && session.ResumeState == SessionState.Revealed);

    /// <summary>
    /// 已解锁时可公开的真相原文。只取 Visibility == Truth 的段；未解锁时返回空集合。
    /// 私有段（角色本）任何时候都不在这里。
    /// </summary>
    public static IReadOnlyList<string> RevealedTexts(ScriptPackage package, Session session)
    {
        if (!IsUnlocked(session))
        {
            return Array.Empty<string>();
        }

        // 段数不多，但每次投影/口播都要查一遍：用字典查比线性扫快。
        var byId = package.Segments.ToDictionary(s => s.SegmentId, StringComparer.Ordinal);
        return package.Truth.RevealedSegmentIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(s => s is not null && s.Visibility == SegmentVisibility.Truth)
            .Select(s => s!.OriginalText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }
}
