using System.Security.Cryptography;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;

namespace LocalDm.Core.Import;

/// <summary>
/// 把已提取的页块来源装配成可开局的 ScriptPackage。
/// 分层由 <see cref="ScriptLayeringRule"/> 决定，不由模型判断。
/// </summary>
public static class RealScriptPackageBuilder
{
    /// <summary>
    /// 分组来源。导入源头本来就知道哪些目录是角色本、哪些是线索卡牌，
    /// 这个事实必须随页一起传下来：靠目录名猜会把角色本误判成线索卡堆，
    /// 那样该角色的私有分组名会被写进公共材料编号，破坏秘密隔离。
    /// </summary>
    public enum ExtractedGroupKind
    {
        /// <summary>未标注：退回 <see cref="IsMaterialGroup"/> 的目录名启发式。</summary>
        Unknown = 0,

        /// <summary>角色本：私有材料，由软件外的纸本或 PDF 分发。</summary>
        Role = 1,

        /// <summary>线索卡牌：按编号发放的公共材料。</summary>
        Material = 2,
    }

    /// <summary>一个已提取的页：来自导入管线的 SourceSpan 集合。</summary>
    public sealed record ExtractedPage
    {
        public required string Group { get; init; }

        /// <summary>
        /// 该分组是角色本还是线索卡牌。导入源头已知的事实，优先于目录名启发式。
        /// </summary>
        public ExtractedGroupKind GroupKind { get; init; } = ExtractedGroupKind.Unknown;

        /// <summary>
        /// 页号：PDF 是文件内的页号；图片取自文件名，卡背等非数字页记为 0。
        /// 分层规则按它判断真相页，所以 PDF 必须给真实页号。
        /// </summary>
        public required int PageNumber { get; init; }

        /// <summary>原文件名（不含扩展名）或 <c>p{页号}</c>，用于区分正面与卡背、避免段 ID 冲突。</summary>
        public string PageLabel { get; init; } = string.Empty;

        public required string Text { get; init; }

        /// <summary>这一页在源文件里的页号；图片固定 1，PDF 用文件内的页号。</summary>
        public int SourcePageNumber { get; init; } = 1;

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

        /// <summary>识别出的角色本分组数。用于核对角色是否全部到位。</summary>
        public int RoleGroupCount { get; init; }

        /// <summary>
        /// 识别出的线索卡牌分组数。为 0 时主持台没有可发放的编号，
        /// 需要人工确认是剧本本就没有线索，还是分组识别失败。
        /// </summary>
        public int MaterialGroupCount { get; init; }

        /// <summary>来源里是否有可用的结构化流程声明。没有时整包不能开局。</summary>
        public bool FlowDeclared { get; init; }

        /// <summary>缺来源依据、需要人工复核的环节标题（不含剧情内容）。</summary>
        public IReadOnlyList<string> StepsNeedingReview { get; init; } = [];

        /// <summary>
        /// 声明里指向不存在对象的引用问题（材料编号 / 段落 ID / 来源分组）。
        /// 这些必须让整包待复核，不能靠删掉引用让流程看起来合法。
        /// </summary>
        public IReadOnlyList<string> ReferenceIssues { get; init; } = [];

        /// <summary>内容指纹：来源 + 玩法配置 + 规范化流程。</summary>
        public string ContentHash { get; init; } = string.Empty;
    }

    public static BuildResult Build(
        string title,
        int playerCount,
        IReadOnlyList<ExtractedPage> pages,
        IReadOnlyList<SourceFile> sourceFiles,
        ScriptLayeringRule? rule = null,
        ScriptFlow? flow = null,
        TabletManifest? tablet = null)
    {
        rule ??= ScriptLayeringRule.Default;

        var spans = pages.Select(p => new SourceSpan
        {
            SpanId = p.SpanId,
            FileId = p.FileId,
            // 页码必须用源文件里的真实页号：校验要拿它逐页核对覆盖，写死 1 会让多页 PDF 永远覆盖不全。
            PageNumber = p.SourcePageNumber,
            BlockNumber = 0,
            ToolLocator = $"doc:{p.FileId}/tier:flash/page:{p.SourcePageNumber}/block:0",
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

        // 角色本 / 线索卡牌的分流：采信导入源头标注的 GroupKind（事实），
        // 未标注时才退回目录名启发式。分组名一旦被误判成线索卡堆，
        // 该私有分组名就会进 MaterialIds，进而进入公共投影 —— 这是秘密隔离的红线。
        var roleGroups = pages
            .Where(p => !string.Equals(p.Group, rule.OrganizerGroupName, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(p.Group)
                        && ResolveGroupKind(p) == ExtractedGroupKind.Role)
            .GroupBy(p => p.Group, StringComparer.Ordinal)
            .Select(g => new RoleIndex
            {
                RoleId = g.Key,
                PrivateMaterialRef = $"角色本：{g.Key}（{g.Count()} 页）",
                SourceSpanIds = g.Select(p => p.SpanId).ToArray(),
            })
            .ToArray();

        var materialItems = pages
            .Where(p => !string.Equals(p.Group, rule.OrganizerGroupName, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(p.Group)
                        && ResolveGroupKind(p) == ExtractedGroupKind.Material)
            .GroupBy(p => p.Group, StringComparer.Ordinal)
            .Select(g => new Material
            {
                MaterialId = g.Key,
                // 统计的是页数（含卡背页），不是牌数；写「张」会让主持人按牌对不上数。
                Label = $"线索卡牌：{g.Key}（{g.Count()} 页）",
                SourceSpanIds = g.Select(p => p.SpanId).ToArray(),
            })
            .ToArray();

        var (phases, referenceIssues) = BuildPhases(flow, publicSegments, materialItems);
        var manifestHash = ManifestHash(sourceFiles);

        // 内容身份：来源 + 玩法配置 + 规范化流程 + 可选的平板清单。
        // 只按来源算指纹的话，同一批材料换成另一套流程会得到完全相同的包身份，
        // 旧存档就会静默接受新流程、跳过新增环节；平板清单同理：
        // 改了名称或切片参数必须得到新版本，旧局才不会静默换卡、换名。
        var contentHash = ContentHash(manifestHash, playerCount, phases, tablet);

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

        // 流程没有来源依据就不许开局：宁可停在 NeedsReview，也不要替剧本编一个默认流程。
        // 这里不给 ScriptValidator 加新参数，是因为「流程是否已声明」是装配阶段的事实。
        if (flow is null || flow.Steps.Count == 0)
        {
            readiness = Blocked(readiness, "FLOW_NOT_DECLARED");
        }
        else
        {
            foreach (var issue in flow.ValidationIssues)
            {
                readiness = Blocked(readiness, issue);
            }

            if (!flow.IsFullyDeclared)
            {
                readiness = Blocked(readiness, "FLOW_STEP_NEEDS_REVIEW");
            }
        }

        // 声明里指向不存在的材料/段落/分组时必须挡住，并点名具体 ID：
        // 悄悄把这些引用过滤掉，会让「要求发完材料」变成「一份材料都没有」的假合法流程。
        foreach (var issue in referenceIssues)
        {
            readiness = Blocked(readiness, issue);
        }

        // 声明了组织者手册的目录名，而导入的分组里没有这个目录：必须挡住。
        // 静默退回默认分层会把这一份剧本的真相页当成普通页，剧透就防不住了。
        if (flow?.Layering is { } layering
            && !string.IsNullOrWhiteSpace(layering.OrganizerGroup)
            && !pages.Any(page => string.Equals(page.Group, layering.OrganizerGroup, StringComparison.Ordinal)))
        {
            readiness = Blocked(readiness, $"LAYERING_ORGANIZER_GROUP_MISSING:{layering.OrganizerGroup}");
        }

        var package = new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-" + contentHash[..12],
            Version = "1",
            Title = title,
            PlayerCount = playerCount,
            ManifestHash = manifestHash,
            ContentHash = contentHash,
            SourceFiles = sourceFiles,
            Spans = spans,
            Segments = segments,
            Roles = roleGroups,
            Phases = phases,
            Materials = materialItems,
            Truth = new TruthSection
            {
                RevealedSegmentIds = truthSegments.Select(s => s.SegmentId).ToArray(),
                PrivateText = string.Join("\n", truthSegments.Select(s => s.OriginalText)),
                IsConfirmedByHuman = false,
            },
            Validation = readiness.CanStart
                ? PackageValidationState.Ready
                : PackageValidationState.NeedsReview,
            Tablet = tablet,
        };

        return new BuildResult
        {
            Package = package,
            Readiness = readiness,
            SegmentCounts = segments
                .GroupBy(s => s.Visibility)
                .ToDictionary(g => g.Key, g => g.Count()),
            TruthCharacters = truthSegments.Sum(s => s.OriginalText.Length) + 0 * ignored,
            RoleGroupCount = roleGroups.Length,
            MaterialGroupCount = materialItems.Length,
            FlowDeclared = flow is { Steps.Count: > 0 },
            StepsNeedingReview = flow?.StepsNeedingReview ?? [],
            ReferenceIssues = referenceIssues,
            ContentHash = contentHash,
        };
    }

    /// <summary>
    /// 给一个已经冻结的包补上平板清单，并按新清单重算内容身份。
    ///
    /// 重新准备会得到新版本：清单里的名称、切片与资产都算进内容指纹，
    /// 旧存档不会静默接受新资产、新名字。
    /// </summary>
    public static ScriptPackage WithTabletManifest(ScriptPackage package, TabletManifest tablet)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(tablet);

        var contentHash = ContentHash(package.ManifestHash, package.PlayerCount, package.Phases, tablet);
        return package with
        {
            // 包标识跟着内容指纹走：重新准备得到的是另一份包，
            // 旧存档不会静默接受新资产、新名字。
            PackageId = "pkg-" + contentHash[..12],
            Tablet = tablet,
            ContentHash = contentHash,
        };
    }

    /// <summary>
    /// 规范化流程 → 内容指纹。用「生成的阶段」而不是声明原文来算，
    /// 是因为真正影响玩法的是阶段本身：两份声明只要产出的阶段一样，就是同一份剧本。
    ///
    /// 平板清单只在存在时才写进指纹：没有清单的包与改动前逐字节相同，
    /// 旧存档不会因为这次升级被判定成另一份剧本。
    /// </summary>
    private static string ContentHash(
        string manifestHash,
        int playerCount,
        IReadOnlyList<ScriptPhase> phases,
        TabletManifest? tablet)
    {
        var builder = new StringBuilder();
        builder.Append("schema=").Append(ScriptPackage.CurrentSchemaVersion)
            .Append("|manifest=").Append(manifestHash)
            .Append("|players=").Append(playerCount)
            .Append('\n');

        foreach (var phase in phases)
        {
            builder.Append("phase=").Append(phase.PhaseId)
                .Append('|').Append(phase.Title)
                .Append('|').Append(string.Join(',', phase.AllowedActions.Select(a => (int)a)))
                .Append('|').Append(string.Join(',', phase.MaterialIds))
                .Append('|').Append(phase.IsVotePhase ? '1' : '0')
                .Append('|').Append(phase.IsRevealPhase ? '1' : '0')
                .Append('|').Append(phase.RequiresAllMaterials ? '1' : '0')
                .Append('|').Append(phase.OffersOpeningSpeech ? '1' : '0')
                .Append('|').Append(phase.Hint ?? string.Empty)
                .Append('|').Append(phase.AdvanceLabel ?? string.Empty)
                .Append('|').Append(phase.RevealLabel ?? string.Empty)
                .Append('|').Append(string.Join(',', phase.PublicSegmentIds))
                .Append('|').Append(string.Join(',', phase.Conditions))
                .Append('\n');
        }

        if (tablet is not null)
        {
            builder.Append("tablet=").Append(tablet.ManifestVersion)
                .Append('|').Append(tablet.AssetRootFingerprint)
                .Append('\n');

            foreach (var group in tablet.Groups.Values.OrderBy(g => g.GroupName, StringComparer.Ordinal))
            {
                builder.Append("group=").Append(group.GroupName)
                    .Append('|').Append(group.IsReady ? '1' : '0')
                    .Append('|').Append((int)(group.Reason ?? TabletNotReadyReason.CropUnconfirmed))
                    .Append('|').Append(group.NotReadyCardCount)
                    .Append('\n');
            }

            foreach (var card in tablet.Cards.Values.OrderBy(c => c.CardId, StringComparer.Ordinal))
            {
                builder.Append("card=").Append(card.CardId)
                    .Append('|').Append(card.DisplayName)
                    .Append('|').Append(card.GroupName)
                    .Append('|').Append(card.SourceOrder)
                    .Append('|').Append(card.FrontAssetId)
                    .Append('|').Append(card.BackAssetId ?? string.Empty)
                    .Append('|').Append((int)card.PreparationState)
                    .Append('\n');
            }

            foreach (var asset in tablet.Assets.Values.OrderBy(a => a.AssetId, StringComparer.Ordinal))
            {
                builder.Append("asset=").Append(asset.AssetId)
                    .Append('|').Append(asset.RelativePath)
                    .Append('|').Append(asset.ContentHash)
                    .Append('|').Append((int)asset.Kind)
                    .Append('|').Append(asset.SourceFileId)
                    .Append('|').Append(asset.SourceContentHash ?? string.Empty)
                    .Append('|').Append(asset.SourcePageNumber)
                    .Append('|').Append(asset.GridRows?.ToString() ?? string.Empty)
                    .Append('|').Append(asset.GridColumns?.ToString() ?? string.Empty)
                    .Append('|').Append(asset.GridMirrorHorizontally ? '1' : '0')
                    .Append('|').Append(asset.GridRegion is { } gridRegion
                        ? $"{gridRegion.X},{gridRegion.Y},{gridRegion.Width},{gridRegion.Height}"
                        : string.Empty)
                    .Append('|').Append(asset.CropRegion is { } cropRegion
                        ? $"{cropRegion.X},{cropRegion.Y},{cropRegion.Width},{cropRegion.Height}"
                        : string.Empty)
                    .Append('|').Append(asset.Slot is { } slot ? $"{slot.Row},{slot.Column}" : string.Empty)
                    .Append('\n');
            }
        }

        return Sha256Text(builder.ToString());
    }

    /// <summary>把一条阻塞问题并进就绪报告：CanStart 归零，状态降为 NeedsReview。</summary>
    private static ReadinessReport Blocked(ReadinessReport report, string issue) => report with
    {
        CanStart = false,
        Status = ReadinessStatus.NeedsReview,
        IssueCategories = [.. report.IssueCategories, issue],
    };

    /// <summary>
    /// 识别材料与线索卡组（含楼层、地点、回忆、抉择、卡牌等编号），不与人物角色本混淆。
    ///
    /// 这是<b>兜底</b>启发式：只在 <see cref="ExtractedPage.GroupKind"/> 未标注时使用。
    /// 导入源头已经知道哪些目录是角色本、哪些是线索卡牌，那种情况下不再猜。
    /// </summary>
    public static bool IsMaterialGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return false;
        }

        // 角色本优先：目录名里带「角色」的一律按私有角色本处理。
        // 否则「玉书-角色本」这类名字会被下面的 '-' 规则误判成线索卡，
        // 而误判的代价是把私有分组名写进公共材料编号。
        if (group.Contains("角色", StringComparison.Ordinal))
        {
            return false;
        }

        return group.Contains('-')
               || group.Contains('楼')
               || group.Contains('室')
               || group.Contains("房间")
               || group.Contains("现场")
               || group.Contains("抉择")
               || group.Contains("回忆")
               || group.Contains("线索");
    }

    /// <summary>
    /// 该分组到底算角色本还是线索卡牌：优先采信导入源头标注的事实，
    /// 未标注时才退回目录名启发式。
    /// </summary>
    public static ExtractedGroupKind ResolveGroupKind(ExtractedPage page) =>
        page.GroupKind != ExtractedGroupKind.Unknown
            ? page.GroupKind
            : IsMaterialGroup(page.Group) ? ExtractedGroupKind.Material : ExtractedGroupKind.Role;

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
    /// 按来源声明的流程生成阶段：环节数量、顺序、标题、提示、动作与材料全部来自
    /// <see cref="ScriptFlow"/>，不套固定七步，也不内置第二套「通用」流程。
    ///
    /// 没有可用声明时只生成一个「流程待确认」阶段，且整包已被标为 NeedsReview：
    /// 说清缺什么，比编一个看起来合理的流程更安全。
    ///
    /// 声明里指向不存在的材料编号、段落 ID 或来源分组时，把具体 ID 作为阻塞问题返回，
    /// 而不是悄悄过滤掉 —— 过滤会让「要求材料发完」变成「一份材料都没有」的假合法流程。
    /// </summary>
    private static (IReadOnlyList<ScriptPhase> Phases, IReadOnlyList<string> Issues) BuildPhases(
        ScriptFlow? flow,
        IReadOnlyList<CanonicalSegment> publicSegments,
        IReadOnlyList<Material> materials)
    {
        if (flow is null || flow.Steps.Count == 0)
        {
            return
            (
                [
                    new ScriptPhase
                    {
                        PhaseId = "phase-flow-unresolved",
                        Title = "流程待确认",
                        PublicSegmentIds = [],
                        // 没有 Start：这份包连开局都不该被允许，而不是开了局再卡在这里。
                        AllowedActions = [PublicAction.Pause],
                        Conditions = ["这份剧本还没有可核对的环节流程。请按剧本原文声明各环节后再开局。"],
                        MaterialIds = [],
                        IsVotePhase = false,
                        IsRevealPhase = false,
                        SourceSpanIds = [],
                        Hint = "还没有可核对的流程，暂时不能开局。",
                    },
                ],
                []
            );
        }

        var knownMaterialIds = materials.Select(m => m.MaterialId).ToHashSet(StringComparer.Ordinal);
        var knownSegmentIds = publicSegments.Select(s => s.SegmentId).ToHashSet(StringComparer.Ordinal);
        var phases = new List<ScriptPhase>(flow.Steps.Count);
        var issues = new List<string>();

        for (var i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            var next = i + 1 < flow.Steps.Count ? flow.Steps[i + 1] : null;

            // 只认包里真实存在的材料编号，但每一个不存在的编号都要点名报出来。
            var stepMaterials = new List<string>();
            foreach (var id in step.MaterialIds)
            {
                if (knownMaterialIds.Contains(id))
                {
                    stepMaterials.Add(id);
                }
                else
                {
                    issues.Add($"UNKNOWN_MATERIAL_REFERENCE:{step.StepId}:{id}");
                }
            }

            var (segmentIds, segmentIssues) = ResolvePublicSegments(step, publicSegments, knownSegmentIds);
            issues.AddRange(segmentIssues);

            phases.Add(new ScriptPhase
            {
                PhaseId = step.StepId,
                Title = step.Title,
                PublicSegmentIds = segmentIds,
                AllowedActions = ActionsFor(step.Kind, isFirst: i == 0, hasNext: next is not null),
                Conditions = step.Conditions,
                MaterialIds = stepMaterials,
                IsVotePhase = step.Kind == ScriptStepKind.Vote,
                IsRevealPhase = step.Kind == ScriptStepKind.Reveal,
                SourceSpanIds = publicSegments
                    .Where(s => segmentIds.Contains(s.SegmentId, StringComparer.Ordinal))
                    .SelectMany(s => s.SourceSpanIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Hint = step.Hint,
                // 动作名随剧本走：有下一环节就写清进哪儿，没有就不给一个点了会报错的按钮。
                AdvanceLabel = step.AdvanceLabel
                               ?? (next is null ? null : "进入" + next.Title),
                RevealLabel = step.RevealLabel,
                RequiresAllMaterials = step.RequiresAllMaterials,
                OffersOpeningSpeech = step.OffersOpeningSpeech,
                SourceRef = step.SourceRef,
            });
        }

        return (phases, issues);
    }

    /// <summary>
    /// 环节允许的动作由它在玩法上的类别决定，不由中文标题猜。
    /// 第一个环节额外允许 Start；最后一个环节不再给 Advance（无处可去）。
    /// </summary>
    private static IReadOnlyList<PublicAction> ActionsFor(ScriptStepKind kind, bool isFirst, bool hasNext)
    {
        var actions = new List<PublicAction>(4);
        if (isFirst)
        {
            actions.Add(PublicAction.Start);
        }

        if (hasNext)
        {
            actions.Add(PublicAction.Advance);
        }

        switch (kind)
        {
            case ScriptStepKind.Search:
                // 即使一个线索组都没识别出来也保留：按钮只报告「没有待发编号」，
                // 比悄悄把动作拿掉更容易判断问题出在识别还是剧本本身。
                actions.Add(PublicAction.ReleaseMaterials);
                break;
            case ScriptStepKind.Vote:
                actions.Add(PublicAction.SubmitVoteSummary);
                break;
            case ScriptStepKind.Reveal:
                actions.Add(PublicAction.Reveal);
                break;
        }

        actions.Add(PublicAction.Pause);
        return actions;
    }

    /// <summary>
    /// 环节可公开的段落：优先用声明里写死的段 ID，其次按来源分组名解析，
    /// 两者都没有就保持为空 —— 空白比猜一段原文安全。
    ///
    /// 但「写了却指不到」必须报出来：那是声明写错了，不是「这个环节本来就没有公开内容」。
    /// </summary>
    private static (IReadOnlyList<string> SegmentIds, IReadOnlyList<string> Issues) ResolvePublicSegments(
        ScriptFlowStep step,
        IReadOnlyList<CanonicalSegment> publicSegments,
        IReadOnlySet<string> knownSegmentIds)
    {
        var issues = new List<string>();

        if (step.PublicSegmentIds.Count > 0)
        {
            var resolved = new List<string>(step.PublicSegmentIds.Count);
            foreach (var id in step.PublicSegmentIds)
            {
                if (knownSegmentIds.Contains(id))
                {
                    resolved.Add(id);
                }
                else
                {
                    issues.Add($"UNKNOWN_SEGMENT_REFERENCE:{step.StepId}:{id}");
                }
            }

            return (resolved, issues);
        }

        if (step.PublicGroups.Count == 0)
        {
            return ([], issues);
        }

        var resolvedIds = new List<string>();
        foreach (var group in step.PublicGroups)
        {
            var prefix = $"seg-{group}-";
            var matched = publicSegments
                .Where(s => s.SegmentId.StartsWith(prefix, StringComparison.Ordinal))
                .Select(s => s.SegmentId)
                .ToArray();

            if (matched.Length == 0)
            {
                // 分组名写错或这一组全是私有/真相页：声明与来源对不上，必须挡住。
                issues.Add($"UNKNOWN_PUBLIC_GROUP:{step.StepId}:{group}");
                continue;
            }

            resolvedIds.AddRange(matched);
        }

        return (resolvedIds, issues);
    }
}
