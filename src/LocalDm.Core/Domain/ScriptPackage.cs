namespace LocalDm.Core.Domain;

/// <summary>
/// 冻结剧本包。首版一个固定剧本先验收；准备完成后只读，变更生成新版本，不改已有局。
/// 应用数据模型 C01 / C02 / C04 / C05。
/// </summary>
public sealed record ScriptPackage
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required string PackageId { get; init; }

    public required string Version { get; init; }

    public required string Title { get; init; }

    /// <summary>首版仅支持 4、5、6；此值不是软件内账号数量。</summary>
    public required int PlayerCount { get; init; }

    /// <summary>包指纹：绑定源文件 hash、页数与来源索引，用于开局与恢复校验。</summary>
    public required string ManifestHash { get; init; }

    /// <summary>
    /// 内容指纹：来源 + 玩法配置 + 规范化流程。
    ///
    /// <see cref="ManifestHash"/> 只描述原始材料；同一批材料换成另一套流程时它是同一个值。
    /// 判「这是不是同一份剧本」必须看内容指纹，否则改写流程后旧存档会静默接受新包、跳过新环节。
    /// 旧包没有这一项，读出来为 null，退回 <see cref="ManifestHash"/>。
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>绑定校验用的内容标识：新包用 <see cref="ContentHash"/>，旧包退回 <see cref="ManifestHash"/>。</summary>
    public string Identity => string.IsNullOrWhiteSpace(ContentHash) ? ManifestHash : ContentHash;

    public required IReadOnlyList<SourceFile> SourceFiles { get; init; }

    public required IReadOnlyList<SourceSpan> Spans { get; init; }

    public required IReadOnlyList<CanonicalSegment> Segments { get; init; }

    /// <summary>角色仅保存外部私人材料索引；私密正文不进入公共界面读取路径。</summary>
    public required IReadOnlyList<RoleIndex> Roles { get; init; }

    public required IReadOnlyList<ScriptPhase> Phases { get; init; }

    public required IReadOnlyList<Material> Materials { get; init; }

    /// <summary>真相单独位于私有段，公共投影不包含此字段。</summary>
    public required TruthSection Truth { get; init; }

    public required PackageValidationState Validation { get; init; }
}

public sealed record RoleIndex
{
    public required string RoleId { get; init; }

    /// <summary>外部私人材料定位（软件外的 PDF/纸本/编号包），不存私密正文。</summary>
    public required string PrivateMaterialRef { get; init; }

    public required IReadOnlyList<string> SourceSpanIds { get; init; }
}

public sealed record ScriptPhase
{
    public required string PhaseId { get; init; }

    public required string Title { get; init; }

    /// <summary>公开口播片段 ID（只允许 Public 段）。</summary>
    public required IReadOnlyList<string> PublicSegmentIds { get; init; }

    public required IReadOnlyList<PublicAction> AllowedActions { get; init; }

    /// <summary>原本条件：文本来自剧本原文，由主持窗口无剧透展示。</summary>
    public required IReadOnlyList<string> Conditions { get; init; }

    /// <summary>该阶段释放的材料包编号。</summary>
    public required IReadOnlyList<string> MaterialIds { get; init; }

    public required bool IsVotePhase { get; init; }

    public required bool IsRevealPhase { get; init; }

    public required IReadOnlyList<string> SourceSpanIds { get; init; }

    /// <summary>
    /// 本环节的一句话当前提示（左侧「当前流程提示」）。
    /// 为空时界面退回按 <see cref="Conditions"/> 拼接，不编造额外说明。
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// 推进到下一环节的动作名，例如「进入第二轮搜证」。
    /// 为空时界面按下一环节标题生成，不写死「下一环节」。
    /// </summary>
    public string? AdvanceLabel { get; init; }

    /// <summary>揭秘动作名，例如「公开结局」；为空时用「公开真相」。</summary>
    public string? RevealLabel { get; init; }

    /// <summary>
    /// 本环节提供「请主持人开场」动作。由流程声明决定，
    /// 不按「是不是第一个环节」推断。
    /// </summary>
    public bool OffersOpeningSpeech { get; init; }

    /// <summary>
    /// 本环节的材料必须逐个发放完毕才能推进。
    /// 这是机器可校验的条件，与「点一下按钮就算大家准备好」是两回事。
    /// </summary>
    public bool RequiresAllMaterials { get; init; }

    /// <summary>
    /// 该环节在剧本来源里的依据位置。为空表示流程本身尚未核实，
    /// 准备器据此把整包标为 NeedsReview，而不是补一个默认流程。
    /// </summary>
    public string? SourceRef { get; init; }
}

public sealed record Material
{
    public required string MaterialId { get; init; }

    public required string Label { get; init; }

    /// <summary>对外只显示编号与标签，不显示内容。</summary>
    public required IReadOnlyList<string> SourceSpanIds { get; init; }
}

/// <summary>
/// 私有真相段。只能被工程内部使用；不在公共投影、日志、TTS 与普通问答上下文中出现。
/// </summary>
public sealed record TruthSection
{
    public required IReadOnlyList<string> RevealedSegmentIds { get; init; }

    /// <summary>供自动化测试与人工核验的原始真相文本，不进入任何公共界面。</summary>
    public required string PrivateText { get; init; }

    public required bool IsConfirmedByHuman { get; init; }
}

public enum PackageValidationState
{
    /// <summary>未知事实保留 NeedsReview，不生成默认凶手。</summary>
    NeedsReview = 0,

    Ready = 1,

    Failed = 2,
}
