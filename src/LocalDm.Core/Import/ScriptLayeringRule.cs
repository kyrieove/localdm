using LocalDm.Core.Domain;

namespace LocalDm.Core.Import;

/// <summary>
/// 真实剧本的分层规则：由组织者手册里的作者原话划界，不由程序猜。
/// 依据是组织者手册第 7 页原文：「警告！后面是案件的全部真相，请不要在游戏结束前打开观看。」
/// </summary>
public sealed record ScriptLayeringRule
{
    /// <summary>组织者手册所在的目录名（原文件名就叫「组织」）。</summary>
    public required string OrganizerGroupName { get; init; }

    /// <summary>组织者手册里可作为公共口播的页（含首页与开场信）。</summary>
    public required IReadOnlyList<int> PublicOrganizerPages { get; init; }

    /// <summary>主持人私有页：流程说明、发放时机等，不上公共屏也不朗读。</summary>
    public required IReadOnlyList<int> HostOnlyOrganizerPages { get; init; }

    /// <summary>分界页：只作分层标识，不进入任何阶段。</summary>
    public required IReadOnlyList<int> MarkerPages { get; init; }

    /// <summary>真相页：只有揭秘后才允许公开。</summary>
    public required IReadOnlyList<int> TruthPages { get; init; }

    /// <summary>不纳入的页（版权封底等）。</summary>
    public required IReadOnlyList<int> IgnoredPages { get; init; }

    public static ScriptLayeringRule Default { get; } = new()
    {
        OrganizerGroupName = "组织",
        PublicOrganizerPages = [1, 2],
        HostOnlyOrganizerPages = [3, 4, 5, 6],
        MarkerPages = [7],
        TruthPages = [8, 9, 10, 11],
        IgnoredPages = [12],
    };

    /// <summary>判断某一页属于哪一层；角色本永远为 Private。</summary>
    public SegmentVisibility VisibilityFor(string groupName, int pageNumber)
    {
        if (!string.Equals(groupName, OrganizerGroupName, StringComparison.Ordinal))
        {
            // 角色本与线索卡：整本都不上公共屏
            return SegmentVisibility.Private;
        }

        if (TruthPages.Contains(pageNumber))
        {
            return SegmentVisibility.Truth;
        }

        if (PublicOrganizerPages.Contains(pageNumber))
        {
            return SegmentVisibility.Public;
        }

        return SegmentVisibility.Private;
    }

    /// <summary>这一页是否完全不进入剧本包。</summary>
    public bool IsIgnored(string groupName, int pageNumber) =>
        string.Equals(groupName, OrganizerGroupName, StringComparison.Ordinal)
        && (IgnoredPages.Contains(pageNumber) || MarkerPages.Contains(pageNumber));
}
