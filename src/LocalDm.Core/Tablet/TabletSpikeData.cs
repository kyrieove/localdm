namespace LocalDm.Core.Tablet;

/// <summary>平板上的一个角色：只有标识与显示名，不含任何私密内容。</summary>
public sealed record TabletRole(string RoleId, string DisplayName);

/// <summary>合成卡片目录项：名称、分组与来源序号。</summary>
public sealed record TabletSpikeCard(string CardId, string DisplayName, string GroupName, int SourceOrder);

/// <summary>
/// 合成材料探针数据：角色、卡片目录与角色本文本。
/// 资产目录里的文件在探针启动时生成（合成 PNG），不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class TabletSpikeData
{
    public required IReadOnlyList<TabletRole> Roles { get; init; }

    public required IReadOnlyList<TabletSpikeCard> Cards { get; init; }

    /// <summary>私有资产根目录；只经 cardId 与页码间接引用，客户端拿不到物理路径。</summary>
    public required string AssetRoot { get; init; }

    /// <summary>剧本名，显示在阅读器顶部。</summary>
    public required string StoryTitle { get; init; }

    /// <summary>每个角色的阅读材料（合成文本）。</summary>
    public required IReadOnlyDictionary<string, RoleBookText> RoleBooks { get; init; }

    /// <summary>合成手册页数；页图文件名为 <c>handbook-{页}.png</c>。</summary>
    public int HandbookPageCount { get; init; }
}
