using System.Security.Cryptography;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Import;

/// <summary>
/// 把已提取的页块来源装配成可开局的 ScriptPackage。
/// 分层由 <see cref="ScriptLayeringRule"/> 决定，不由模型判断。
/// </summary>
public static class RealScriptPackageBuilder
{
    /// <summary>一个已提取的页：来自导入管线的 SourceSpan 集合。</summary>
    public sealed record ExtractedPage
    {
        public required string Group { get; init; }

        /// <summary>原文件名里的页号；卡背等非数字页记为 0。</summary>
        public required int PageNumber { get; init; }

        /// <summary>原文件名（不含扩展名），用于区分正面与卡背，避免段 ID 冲突。</summary>
        public string PageLabel { get; init; } = string.Empty;

        public required string Text { get; init; }

        public required string FileId { get; init; }

        public required string SpanId { get; init; }
    }

    public sealed record BuildResult
    {
        public required ScriptPackage Package { get; init; }

        public required ReadinessReport Readiness { get; init; }

        /// <summary>各可见性下的段数，用于核对分层。</summary>
        public required IReadOnlyDictionary<SegmentVisibility, int> SegmentCounts { get; init; }

        /// <summary>真相段在包内的文本总长度（不输出内容）。</summary>
        public required int TruthCharacters { get; init; }
    }

    public static BuildResult Build(
        string title,
        int playerCount,
        IReadOnlyList<ExtractedPage> pages,
        IReadOnlyList<SourceFile> sourceFiles,
        ScriptLayeringRule? rule = null)
    {
        rule ??= ScriptLayeringRule.Default;

        var spans = pages.Select(p => new SourceSpan
        {
            SpanId = p.SpanId,
            FileId = p.FileId,
            // 每张图片是独立文件，图片的页号固定为 1；真实页序由 manifest 的 order 指定。
            PageNumber = 1,
            BlockNumber = 0,
            ToolLocator = $"doc:{p.FileId}/tier:flash/page:1/block:0",
            TextHash = Sha256Text(p.Text),
            OriginalText = p.Text,
        }).ToArray();

        var fileById = sourceFiles.ToDictionary(f => f.FileId, StringComparer.Ordinal);
        foreach (var span in spans)
        {
            span.Validate(fileById);
        }

        var segments = new List<CanonicalSegment>();
        var ignored = 0;
        foreach (var page in pages)
        {
            if (rule.IsIgnored(page.Group, page.PageNumber))
            {
                ignored++;
                continue;
            }

            segments.Add(new CanonicalSegment
            {
                // 用文件名区分正面与卡背，避免同号页互相覆盖
                SegmentId = string.IsNullOrEmpty(page.PageLabel)
                    ? $"seg-{page.Group}-p{page.PageNumber}"
                    : $"seg-{page.Group}-{page.PageLabel}",
                OriginalText = page.Text,
                SourceSpanIds = [page.SpanId],
                Visibility = rule.VisibilityFor(page.Group, page.PageNumber),
                ReleasePhaseId = null,
            });
        }

        foreach (var segment in segments)
        {
            segment.Validate();
        }

        var publicSegments = segments.Where(s => s.Visibility == SegmentVisibility.Public).ToArray();
        var truthSegments = segments.Where(s => s.Visibility == SegmentVisibility.Truth).ToArray();

        var phases = BuildPhases(publicSegments, truthSegments);
        var manifestHash = ManifestHash(sourceFiles);

        var keyFacts = segments
            .Where(s => s.Visibility != SegmentVisibility.Private)
            .Select(s => new KeyFact
            {
                FactId = s.SegmentId,
                Text = s.OriginalText,
                SourceSpanIds = s.SourceSpanIds,
                IsResolved = true,
            })
            .ToArray();

        var readiness = ScriptValidator.Validate(
            title,
            playerCount,
            sourceFiles,
            spans,
            segments,
            keyFacts,
            toolComplete: true,
            continuationPending: false);

        var package = new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-" + manifestHash[..12],
            Version = "1",
            Title = title,
            PlayerCount = playerCount,
            ManifestHash = manifestHash,
            SourceFiles = sourceFiles,
            Spans = spans,
            Segments = segments,
            Roles = pages
                .Where(p => !string.Equals(p.Group, rule.OrganizerGroupName, StringComparison.Ordinal))
                .GroupBy(p => p.Group, StringComparer.Ordinal)
                .Select(g => new RoleIndex
                {
                    RoleId = g.Key,
                    PrivateMaterialRef = $"外部材料包：{g.Key}（{g.Count()} 页）",
                    SourceSpanIds = g.Select(p => p.SpanId).ToArray(),
                })
                .ToArray(),
            Phases = phases,
            Materials = [],
            Truth = new TruthSection
            {
                RevealedSegmentIds = truthSegments.Select(s => s.SegmentId).ToArray(),
                PrivateText = string.Join("\n", truthSegments.Select(s => s.OriginalText)),
                IsConfirmedByHuman = false,
            },
            Validation = readiness.CanStart
                ? PackageValidationState.Ready
                : PackageValidationState.NeedsReview,
        };

        return new BuildResult
        {
            Package = package,
            Readiness = readiness,
            SegmentCounts = segments
                .GroupBy(s => s.Visibility)
                .ToDictionary(g => g.Key, g => g.Count()),
            TruthCharacters = truthSegments.Sum(s => s.OriginalText.Length) + 0 * ignored,
        };
    }

    private static string Sha256Text(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>与存储层的包指纹算法保持一致：按 fileId 排序后拼 hash/页数/顺序。</summary>
    private static string ManifestHash(IEnumerable<SourceFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.FileId, StringComparer.Ordinal))
        {
            builder.Append(file.FileId).Append('|')
                .Append(file.ContentHash).Append('|')
                .Append(file.PageCount).Append('|')
                .Append((int)file.Origin).Append('\n');
        }

        return Sha256Text(builder.ToString());
    }

    /// <summary>
    /// 阶段按剧本既有流程：阅本 → 开场说明 → 搜证 → 讨论 → 投票 → 真相 → 复盘。
    /// 公开口播只引用 Public 段；真相段只在揭秘阶段解锁。
    /// </summary>
    private static IReadOnlyList<ScriptPhase> BuildPhases(
        IReadOnlyList<CanonicalSegment> publicSegments,
        IReadOnlyList<CanonicalSegment> truthSegments)
    {
        string[] introSegments = publicSegments.Select(s => s.SegmentId).Take(1).ToArray();
        string[] rulesSegments = publicSegments.Select(s => s.SegmentId).Skip(1).Take(1).ToArray();

        ScriptPhase Phase(
            string id,
            string title,
            IReadOnlyList<string> segments,
            IReadOnlyList<PublicAction> actions,
            IReadOnlyList<string> conditions,
            bool vote = false,
            bool reveal = false) => new()
        {
            PhaseId = id,
            Title = title,
            PublicSegmentIds = segments,
            AllowedActions = actions,
            Conditions = conditions,
            RequiresGroupReady = true,
            MaterialIds = [],
            IsVotePhase = vote,
            IsRevealPhase = reveal,
            SourceSpanIds = segments.Count > 0 ? ["span-" + id] : [],
        };

        return
        [
            Phase("phase-read", "阅本",
                introSegments,
                [PublicAction.Advance, PublicAction.Pause],
                ["全员拿到角色本并开始阅读", "读完自己的剧本"]),
            Phase("phase-rules", "开场说明",
                rulesSegments,
                [PublicAction.Advance, PublicAction.Pause],
                ["主持人已说明规则与时长"]),
            Phase("phase-search", "搜证",
                [],
                [PublicAction.Advance, PublicAction.Pause],
                ["按剧本规定完成搜证", "线索已按编号发放"]),
            Phase("phase-discuss", "讨论",
                [],
                [PublicAction.Advance, PublicAction.Pause],
                ["玩家自由讨论", "主持人等待玩家主动发起"]),
            Phase("phase-vote", "投票",
                [],
                [PublicAction.Advance, PublicAction.SubmitVoteSummary],
                ["纸笔投票完成", "主持人录入汇总并二次确认"],
                vote: true),
            Phase("phase-reveal", "真相",
                [],
                [PublicAction.Advance, PublicAction.Reveal],
                ["投票结果已提交"],
                reveal: true),
            Phase("phase-review", "复盘",
                [],
                [PublicAction.Advance],
                ["真相已公开，按剧本复盘"]),
        ];
    }
}
