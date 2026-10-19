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

    /// <summary>推进该阶段需要勾选「大家已口头准备好」。</summary>
    public required bool RequiresGroupReady { get; init; }

    /// <summary>该阶段释放的材料包编号。</summary>
    public required IReadOnlyList<string> MaterialIds { get; init; }

    public required bool IsVotePhase { get; init; }

    public required bool IsRevealPhase { get; init; }

    public required IReadOnlyList<string> SourceSpanIds { get; init; }
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
