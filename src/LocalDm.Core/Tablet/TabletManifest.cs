namespace LocalDm.Core.Tablet;

/// <summary>
/// 冻结剧本包里的平板清单（可选）。
///
/// 存在理由：平板要按「名称」列出全部线索卡，玩家点哪张就取哪张。
/// 名称由人认、图由程序取：名称只用来让人挑，取图只走 <see cref="TabletCard.CardId"/>
/// 与 <see cref="TabletAsset.AssetId"/>，不按名称拼路径，也不模糊命中取第一张。
///
/// 这里<b>没有任何按环节解锁的判断</b>：这是已确认的产品决定，
/// 全部已准备好的卡从加入后就可访问，何时点由玩家遵循规则（ADR 0005）。
///
/// 旧包没有这一段时它是一个 null，纸质模式照常；平板要重新准备后才能用。
/// </summary>
public sealed record TabletManifest
{
    public const int CurrentManifestVersion = 1;

    public required int ManifestVersion { get; init; }

    /// <summary>
    /// 资产目录的包内容指纹。资产按它隔离存放：换一版包不会覆盖上一版的图，
    /// 旧页面也取不到新局的同号卡。
    /// </summary>
    public required string AssetRootFingerprint { get; init; }

    public required IReadOnlyDictionary<string, TabletCard> Cards { get; init; }

    public required IReadOnlyDictionary<string, TabletGroupPreparationState> Groups { get; init; }

    public required IReadOnlyDictionary<string, TabletAsset> Assets { get; init; }

    /// <summary>准备完成的时间；人工重做准备会换一个新包版本。</summary>
    public DateTimeOffset? PreparedAt { get; init; }

    /// <summary>空清单：旧包或还没准备的包用它，目录为空、什么都取不到。</summary>
    public static TabletManifest Empty { get; } = new()
    {
        ManifestVersion = CurrentManifestVersion,
        AssetRootFingerprint = new string('0', 64),
        Cards = new Dictionary<string, TabletCard>(StringComparer.Ordinal),
        Groups = new Dictionary<string, TabletGroupPreparationState>(StringComparer.Ordinal),
        Assets = new Dictionary<string, TabletAsset>(StringComparer.Ordinal),
    };
}

/// <summary>
/// 一张线索卡的固定索引。
///
/// <see cref="CardId"/> 是取图的唯一入口；<see cref="DisplayName"/> 只是给人看的，
/// 可以改，改名不动 <see cref="CardId"/> 与资产引用，所以改名不会换错卡。
/// </summary>
public sealed record TabletCard
{
    /// <summary>稳定卡片 ID：同一张卡重新准备也保持不变，玩家点选与取图都靠它。</summary>
    public required string CardId { get; init; }

    /// <summary>公开名称。来自卡背已有名称或卡面印刷的标题/卡号，不由模型概括剧情。</summary>
    public required string DisplayName { get; init; }

    /// <summary>所属分组（导入时的线索分组名），同名卡靠它与编号区分。</summary>
    public required string GroupName { get; init; }

    /// <summary>在来源分组里的序号（1 起），用于排目录和区分同名卡。</summary>
    public required int SourceOrder { get; init; }

    /// <summary>卡面资产 ID。</summary>
    public required string FrontAssetId { get; init; }

    /// <summary>卡背资产 ID；没有卡背时为 null。</summary>
    public string? BackAssetId { get; init; }

    public required TabletCardPreparationState PreparationState { get; init; }

    /// <summary>未准备好的脱敏原因分类；准备好时为 null。</summary>
    public TabletNotReadyReason? NotReadyReason { get; init; }

    /// <summary>来源文件 ID（原文件的 fileId），用于回溯这张卡从哪份材料切出来。</summary>
    public string? SourceFileId { get; init; }

    /// <summary>来源页号（1 起）；单张图片为 1。</summary>
    public int? SourcePageNumber { get; init; }

    /// <summary>整版切出来的卡才有槽位；单卡文件为 null。</summary>
    public GridSlot? Slot { get; init; }

    /// <summary>原文件名（不含扩展名）：名称只有数字时可结合它显示，也作为查找别名。</summary>
    public string? SourceFileName { get; init; }
}

public enum TabletCardPreparationState
{
    /// <summary>可以直接分发：资产齐、组也已确认。</summary>
    Ready = 0,

    /// <summary>不可分发：原因见 <see cref="TabletCard.NotReadyReason"/>，且该组整体不可用。</summary>
    NotReady = 1,
}

/// <summary>
/// 脱敏的未准备好原因分类。写进界面与日志的只有这些类别，不含卡面文字或路径。
/// 对应数据模型里「缺正面、配对冲突、裁切未确认、资产缺失」四类。
/// </summary>
public enum TabletNotReadyReason
{
    /// <summary>组里只有卡背，找不到配对的卡面。</summary>
    MissingFront = 0,

    /// <summary>一个编号或一张图同时对上多个候选，正背面配不唯一。</summary>
    PairingConflict = 1,

    /// <summary>切图质量还没被确认（非规整版面或还没人核过）。</summary>
    CropUnconfirmed = 2,

    /// <summary>清单指向的资产文件不存在或 hash 对不上。</summary>
    AssetMissing = 3,

    /// <summary>卡片还缺名称（只认得出编号），标「待命名」前不供分发。</summary>
    NameMissing = 4,
}

/// <summary>
/// 一组线索卡（一个导入分组）的准备状态。
///
/// 组是最小分发单位：组里任何一张拆不出来，整组都保持未准备好，
/// 不许用整页图代替单张把没拆开的卡混着发出去。
/// </summary>
public sealed record TabletGroupPreparationState
{
    public required string GroupName { get; init; }

    public required TabletGroupReadiness Readiness { get; init; }

    /// <summary>未准备好时占第一位的原因（最该先处理的那个）。</summary>
    public TabletNotReadyReason? Reason { get; init; }

    /// <summary>这一组里没准备好的卡数。为 0 才算整组可用。</summary>
    public int NotReadyCardCount { get; init; }

    public bool IsReady => Readiness == TabletGroupReadiness.Ready;
}

public enum TabletGroupReadiness
{
    /// <summary>整组可以分发。</summary>
    Ready = 0,

    /// <summary>整组暂停分发：不要用整页代替单张，正常玩纸质模式或换材料。</summary>
    NotReady = 1,
}

/// <summary>
/// 一个冻结资产。客户端只会拿到 <see cref="AssetId"/>，经它间接取图；
/// <see cref="RelativePath"/> 是包内相对路径，不是物理路径。
/// </summary>
public sealed record TabletAsset
{
    public required string AssetId { get; init; }

    /// <summary>资产根目录下的相对路径，用 <c>/</c> 分隔。</summary>
    public required string RelativePath { get; init; }

    /// <summary>文件内容 hash（SHA-256，小写十六进制）。</summary>
    public required string ContentHash { get; init; }

    public required TabletAssetKind Kind { get; init; }

    /// <summary>来源文件 ID：切图从哪份原文件出的。</summary>
    public required string SourceFileId { get; init; }

    /// <summary>原文件内容 hash：源文件被换掉时可以立刻发现。</summary>
    public string? SourceContentHash { get; init; }

    public required int SourcePageNumber { get; init; }

    /// <summary>整版切图参数：原图行数、列数、有效区域与镜像设置，用于回溯这张是怎么切出来的。</summary>
    public int? GridRows { get; init; }

    public int? GridColumns { get; init; }

    public bool GridMirrorHorizontally { get; init; }

    public PixelRegion? GridRegion { get; init; }

    /// <summary>实际从原图读取的像素矩形；镜像切图时记录反射后的真实来源区域。</summary>
    public PixelRegion? CropRegion { get; init; }

    public GridSlot? Slot { get; init; }
}

public enum TabletAssetKind
{
    CardFront = 0,

    CardBack = 1,

    /// <summary>角色本原稿页。一期只做线索卡，这类资产先留位给角色本接入。</summary>
    RolePage = 2,
}

/// <summary>整版图上的一格：行、列都从 0 开始，与有效区域左上角对齐。</summary>
public readonly record struct GridSlot(int Row, int Column);

/// <summary>整版图上的像素矩形（左上角含、右下角不含），坐标相对整张原图。</summary>
public readonly record struct PixelRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// 整版切图参数：通用网格，不为任何一本剧本写专属尺寸。
///
/// <see cref="Region"/> 是「有效区域」：整页上真正放卡的那一块。
/// 「5 行 1 列占左半页」必须把有效区域设成左半边，不能把整幅宽度当成单卡宽度。
/// </summary>
public sealed record GridLayout
{
    public required int Rows { get; init; }

    public required int Columns { get; init; }

    public required PixelRegion Region { get; init; }

    /// <summary>源图上的有效物理槽位（行优先或任意顺序都可以）。为空表示整张网格每一格都是卡。</summary>
    public IReadOnlyList<GridSlot> ValidSlots { get; init; } = [];

    /// <summary>这张印版是左右镜像印的：反射槽位与边界后再水平反转卡内像素。</summary>
    public bool MirrorHorizontally { get; init; }

    /// <summary>这一版网格全部有效槽位，按行优先排好。</summary>
    public IReadOnlyList<GridSlot> EffectiveSlots()
    {
        if (ValidSlots.Count == 0)
        {
            var all = new List<GridSlot>(Rows * Columns);
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    all.Add(new GridSlot(row, column));
                }
            }

            return all;
        }

        return ValidSlots
            .OrderBy(slot => slot.Row)
            .ThenBy(slot => slot.Column)
            .ToArray();
    }
}

/// <summary>
/// 正背面槽位映射：背面印版的哪一格，对应正面印版的哪一格。
///
/// 默认是同槽位对应；双面印刷的背面常常左右镜像，
/// 这时要显式打开水平反转。两种都不是对新剧本的事实保证，所以两种都能配。
/// </summary>
public sealed record GridSlotMapping
{
    public required bool Mirrored { get; init; }

    /// <summary>背面印版的列数；用于拒绝越界槽位并在镜像时折回列号。</summary>
    public int BackColumns { get; init; } = 1;

    public static GridSlotMapping SameSlot { get; } = new() { Mirrored = false };

    /// <summary>同槽位对应（不镜像）就是事实上的恒等映射。</summary>
    public static GridSlotMapping DefaultForPairing(bool mirrored, int backColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backColumns);
        return new GridSlotMapping { Mirrored = mirrored, BackColumns = backColumns };
    }

    /// <summary>背面的一格 → 正面的那一格。</summary>
    public GridSlot MapBackHeader(GridSlot backSlot)
    {
        if (backSlot.Row < 0 || backSlot.Column < 0 || backSlot.Column >= BackColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(backSlot), "背面槽位超出了网格范围。");
        }

        return Mirrored
            ? backSlot with { Column = BackColumns - 1 - backSlot.Column }
            : backSlot;
    }
}
