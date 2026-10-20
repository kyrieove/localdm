using LocalDm.Core.Domain;

namespace LocalDm.Tests;

/// <summary>
/// 固定合成剧本包：5 人、五阶段（阅本/搜证/讨论/投票/真相）。
/// 只用于自动化验证，不含任何真实剧本内容；真相文本同样是合成占位。
/// </summary>
public static class SyntheticPackage
{
    public const string PublicIntro = "欢迎来到合成测试剧本，请先阅读第一幕。";
    public const string PublicSearch = "现在开始第一轮搜证，请按编号领取线索。";
    public const string PublicDiscuss = "请就目前的线索进行讨论。";
    public const string PublicVote = "请用纸笔投票，由主持人汇总。";
    public const string PublicTruth = "这是合成真相，只用于验证揭秘投影。";

    /// <summary>角色本正文：属于某个角色，DM 也不该看到。</summary>
    public const string RoleBookText = "这是合成角色的秘密，公共界面不应出现。";

    /// <summary>线索卡正文：属于材料分组，DM 要读、玩家自己翻。</summary>
    public const string ClueCardText = "这是合成线索卡正文，只给 DM 看。";

    /// <summary>主持人专用页正文：既不属于角色也不属于材料。</summary>
    public const string HostOnlyText = "这是合成主持人专用说明。";

    public static ScriptPackage Create()
    {
        var file = new SourceFile
        {
            FileId = "synthetic-file",
            PrivatePath = "synthetic.pdf",
            ContentHash = new string('a', 64),
            PageCount = 8,
            Origin = SourceOrder.PageOrder,
            ToolIdentity = "mineru 4.0.1",
            ParseState = FileParseState.Complete,
        };

        var spans = new[]
        {
            Span("s1", 1), Span("s2", 2), Span("s3", 3), Span("s4", 4), Span("s5", 5),
            Span("s6", 6), Span("s7", 7), Span("s8", 8),
        };

        var segments = new[]
        {
            Segment("seg-intro", PublicIntro, SegmentVisibility.Public, "s1"),
            Segment("seg-search", PublicSearch, SegmentVisibility.Public, "s2"),
            Segment("seg-discuss", PublicDiscuss, SegmentVisibility.Public, "s3"),
            Segment("seg-vote", PublicVote, SegmentVisibility.Public, "s4"),
            Segment("seg-truth", PublicTruth, SegmentVisibility.Truth, "s5"),
            // 角色本：私有，且落在角色自己的来源 span 里
            Segment("seg-secret", RoleBookText, SegmentVisibility.Private, "s6"),
            // 线索卡：私有，但属于材料分组，DM 要读、玩家自己翻
            Segment("seg-clue", ClueCardText, SegmentVisibility.Private, "s7"),
            // 主持人专用页：私有，既不属于任何角色也不属于任何材料
            Segment("seg-host", HostOnlyText, SegmentVisibility.Private, "s8"),
        };

        var phases = new[]
        {
            Phase("phase-read", "阅本", [PublicAction.Start, PublicAction.Advance, PublicAction.Pause], [], false, false, "seg-intro"),
            Phase("phase-search", "搜证", [PublicAction.Advance, PublicAction.ReleaseMaterials, PublicAction.Pause], ["M1", "M2"], false, false, "seg-search"),
            Phase("phase-discuss", "讨论", [PublicAction.Advance, PublicAction.Pause], [], false, false, "seg-discuss"),
            Phase("phase-vote", "投票", [PublicAction.Advance, PublicAction.SubmitVoteSummary], [], true, false, "seg-vote"),
            Phase("phase-reveal", "真相", [PublicAction.Reveal, PublicAction.Advance], [], false, true, "seg-truth"),
        };

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-synthetic",
            Version = "1",
            Title = "合成测试剧本《灯塔回声》",
            PlayerCount = 5,
            ManifestHash = new string('b', 64),
            SourceFiles = [file],
            Spans = spans,
            Segments = segments,
            Roles =
            [
                new RoleIndex { RoleId = "role-1", PrivateMaterialRef = "外部材料包 #1", SourceSpanIds = ["s6"] },
            ],
            Phases = phases,
            Materials =
            [
                new Material { MaterialId = "M1", Label = "线索包一", SourceSpanIds = ["s2", "s7"] },
                new Material { MaterialId = "M2", Label = "线索包二", SourceSpanIds = ["s2"] },
            ],
            Truth = new TruthSection
            {
                RevealedSegmentIds = ["seg-truth"],
                PrivateText = PublicTruth,
                IsConfirmedByHuman = false,
            },
            Validation = PackageValidationState.Ready,
        };
    }

    public static Session NewSession(string sessionId = "session-1") => new()
    {
        SessionId = sessionId,
        PackageId = "pkg-synthetic",
        PackageVersion = "1",
        ManifestHash = new string('b', 64),
        State = SessionState.Prepared,
        CurrentPhaseId = null,
        Revision = 0,
        ReleasedMaterialIds = [],
    };

    public static GameCommand Command(
        GameCommandType type,
        int revision,
        string operationId,
        IReadOnlyList<string>? materials = null,
        VoteSummary? votes = null,
        string sessionId = "session-1") => new()
    {
        SessionId = sessionId,
        OperationId = operationId,
        ExpectedRevision = revision,
        Type = type,
        MaterialIds = materials ?? [],
        Votes = votes,
    };

    private static ScriptPhase Phase(
        string id,
        string title,
        IReadOnlyList<PublicAction> actions,
        IReadOnlyList<string> materials,
        bool isVote,
        bool isReveal,
        string publicSegmentId) => new()
    {
        PhaseId = id,
        Title = title,
        PublicSegmentIds = [publicSegmentId],
        AllowedActions = actions,
        Conditions = [$"原本条件：{title}阶段满足剧本条件"],
        Hint = $"{title}环节当前提示",
        MaterialIds = materials,
        IsVotePhase = isVote,
        IsRevealPhase = isReveal,
        SourceSpanIds = ["s1"],
    };

    private static CanonicalSegment Segment(
        string id,
        string text,
        SegmentVisibility visibility,
        string spanId) => new()
    {
        SegmentId = id,
        OriginalText = text,
        SourceSpanIds = [spanId],
        Visibility = visibility,
    };

    private static SourceSpan Span(string id, int page) => new()
    {
        SpanId = id,
        FileId = "synthetic-file",
        PageNumber = page,
        BlockNumber = 1,
        ToolLocator = $"doc:syn/tier:standard/page:{page}/block:1",
        TextHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("合成页 " + page))),
        OriginalText = "合成页 " + page,
    };
}
