namespace LocalDm.Core.Tablet;

/// <summary>合成角色：探针用，不含任何真实剧本内容。</summary>
public sealed record TabletSpikeRole(string RoleId, string DisplayName, int PageCount);

/// <summary>合成卡片目录项：名称、分组与来源序号。</summary>
public sealed record TabletSpikeCard(string CardId, string DisplayName, string GroupName, int SourceOrder);

/// <summary>
/// 合成探针数据：角色、卡片目录与资产根目录。
/// 资产目录里的文件在探针启动时生成（合成 PNG），不读 stories/、.appdata/、.local/。
/// </summary>
public sealed class TabletSpikeData
{
    public required IReadOnlyList<TabletSpikeRole> Roles { get; init; }

    public required IReadOnlyList<TabletSpikeCard> Cards { get; init; }

    /// <summary>私有资产根目录；只经 cardId 与页码间接引用，客户端拿不到物理路径。</summary>
    public required string AssetRoot { get; init; }
}
