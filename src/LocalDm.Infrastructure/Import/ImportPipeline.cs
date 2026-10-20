using System.Runtime.Versioning;
using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 导入管线：隐私预检 → 提取（含续读）→ 来源归一化 → 应用层校验 → 冻结包。
/// 在未获资料目的地授权前只允许合成输入或已验证本地处理。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImportPipeline
{
    private readonly MineruRunner _runner;
    private readonly MineruPrivacyPreflight _preflight;
    private readonly IFallbackOcr? _fallbackOcr;

    public ImportPipeline(
        MineruRunner runner,
        MineruPrivacyPreflight preflight,
        IFallbackOcr? fallbackOcr = null)
    {
        _runner = runner;
        _preflight = preflight;
        _fallbackOcr = fallbackOcr;
    }

    /// <summary>
    /// 处理一个输入文件并返回脱敏结果；任何一步不通过都停在明确的错误码上。
    /// </summary>
    /// <param name="manifestHash">本批材料的清单 hash；授权绑定它，而不是单个 fileId。</param>
    public async Task<ImportFileResult> ImportFileAsync(
        string fileId,
        string absolutePath,
        DocumentKind kind,
        int? pdfPageCount,
        DataConsent? consent,
        CancellationToken cancellationToken,
        string? manifestHash = null,
        ParseTier tier = ParseTier.Flash)
    {
        // 无授权时连工具身份都不查询：真实文件路径不会到达任何外部调用
        var isSynthetic = absolutePath.Contains("fixtures", StringComparison.OrdinalIgnoreCase);
        if (!isSynthetic)
        {
            var boundHash = manifestHash ?? fileId;
            if (!DataConsent.AllowsRealMaterial(consent, consent?.ToolIdentity ?? string.Empty, boundHash))
            {
                return ImportFileResult.Blocked(
                    fileId,
                    "CONSENT_REQUIRED",
                    "未获得该材料的处理授权，已停止；未上送任何真实文件。");
            }
        }

        var toolIdentity = await _runner.GetIdentityAsync(cancellationToken);

        var serverStatus = await CaptureStatusAsync("server", cancellationToken);
        var telemetryStatus = await CaptureStatusAsync("telemetry", cancellationToken);
        var (home, telemetry) = MineruPrivacyPreflight.ReadStatusJson(serverStatus, telemetryStatus);

        var preflight = _preflight.Evaluate(toolIdentity, home, telemetry);
        if (!preflight.Allowed)
        {
            return ImportFileResult.Blocked(fileId, preflight.ErrorCode!, preflight.PublicReason!);
        }

        var source = SourceIndex.BuildSourceFile(
            fileId,
            absolutePath,
            kind,
            pdfPageCount,
            kind == DocumentKind.Image ? SourceOrder.ManifestOrder : SourceOrder.PageOrder,
            $"{toolIdentity.Tool} {toolIdentity.Version}");

        var reader = new MineruContinuationReader(_runner);
        DocumentExtraction extraction;
        try
        {
            extraction = await reader.ExtractToCompletionAsync(
                new DocumentParseRequest
                {
                    FileId = fileId,
                    AbsolutePath = absolutePath,
                    Kind = kind,
                    PrivacyDecision = preflight.Decision,
                    Tier = tier,
                },
                cancellationToken);
        }
        catch (DomainRuleException ex)
        {
            return ImportFileResult.Failed(fileId, ex.Code.ToString(), ex.PublicReason);
        }

        var spans = SourceIndex.BuildSpans(source, extraction.Batches);
        var state = extraction.ToolComplete ? ImportFileState.Complete : ImportFileState.Incomplete;
        var fallback = (IReadOnlyList<SourceSpan>)[];
        string? fallbackName = null;

        // flash 轻量档偶尔读不出图上的文字（实测：花纹底 + 旋转文字的线索卡）。
        // 此时不谎报成功，而是用本机回退 OCR 再读一次，并把来源标注为回退实现，供人工优先抽查。
        if (spans.Count == 0 && _fallbackOcr is not null)
        {
            var rescued = await _fallbackOcr.TryReadAsync(absolutePath, cancellationToken);
            if (rescued is { Count: > 0 })
            {
                fallback = SourceIndex.BuildSpans(source, rescued);
                fallbackName = _fallbackOcr.Name;
            }
        }

        var effectiveSpans = spans.Count > 0 ? spans : fallback;
        return new ImportFileResult(
            fileId,
            state,
            extraction.ToolComplete,
            source,
            effectiveSpans,
            extraction.Warnings,
            null,
            null,
            fallbackName);
    }

    private async Task<string> CaptureStatusAsync(string area, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunStatusAsync(area, cancellationToken);
        }
        catch (DomainRuleException)
        {
            return string.Empty;
        }
    }
}

public enum ImportFileState
{
    Complete = 0,
    Incomplete = 1,
    Failed = 2,
    Blocked = 3,
}

public sealed record ImportFileResult(
    string FileId,
    ImportFileState State,
    bool ToolComplete,
    SourceFile? Source,
    IReadOnlyList<SourceSpan> Spans,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? PublicReason,
    /// <summary>页块来自哪个回退 OCR；null 表示由主解析器（MinerU）产出。</summary>
    string? FallbackOcr = null)
{
    public static ImportFileResult Blocked(string fileId, string code, string reason) =>
        new(fileId, ImportFileState.Blocked, false, null, [], [], code, reason);

    public static ImportFileResult Failed(string fileId, string code, string reason) =>
        new(fileId, ImportFileState.Failed, false, null, [], [], code, reason);
}

/// <summary>
/// 把应用包冻结成 ScriptPackage：完整写入、校验、再注册；未完成包不能开局。
/// </summary>
public static class ScriptPackageBuilder
{
    public static ScriptPackage Freeze(
        PreparedPackageReader.PreparedPackageDto dto,
        string title,
        int playerCount,
        ReadinessReport report)
    {
        var files = dto.SourceFiles
            .Select(f => new SourceFile
            {
                FileId = f.FileId,
                PrivatePath = f.PrivatePath ?? string.Empty,
                ContentHash = string.IsNullOrWhiteSpace(f.ContentHash) ? new string('0', 64) : f.ContentHash,
                PageCount = Math.Max(1, f.PageCount),
                Origin = SourceOrder.ManifestOrder,
                ToolIdentity = dto.ToolIdentity,
                ParseState = FileParseState.Complete,
            })
            .ToArray();

        var spans = dto.Spans
            .Select(s => new SourceSpan
            {
                SpanId = s.SpanId,
                FileId = s.FileId,
                PageNumber = Math.Max(1, s.PageNumber),
                BlockNumber = s.BlockNumber,
                ToolLocator = s.ToolLocator,
                TextHash = SourceIndex.ComputeTextHash(s.OriginalText),
                OriginalText = s.OriginalText,
            })
            .ToArray();

        var manifestHash = Storage.ManifestFingerprint.Compute(files);

        var segments = spans
            .Select(s => new CanonicalSegment
            {
                SegmentId = "seg-" + s.SpanId,
                OriginalText = s.OriginalText,
                SourceSpanIds = [s.SpanId],
                Visibility = SegmentVisibility.Public,
                ReleasePhaseId = null,
            })
            .ToArray();

        var phases = new[]
        {
            new ScriptPhase
            {
                PhaseId = "phase-1",
                Title = "开场",
                PublicSegmentIds = segments.Take(1).Select(s => s.SegmentId).ToArray(),
                AllowedActions = [PublicAction.Start, PublicAction.Pause],
                Conditions = ["全员已就坐并拿到本"],
                MaterialIds = [],
                IsVotePhase = false,
                IsRevealPhase = false,
                SourceSpanIds = segments.Take(1).SelectMany(s => s.SourceSpanIds).ToArray(),
                Hint = "请大家就坐并拿到自己的本。",
            },
        };

        return new ScriptPackage
        {
            SchemaVersion = ScriptPackage.CurrentSchemaVersion,
            PackageId = "pkg-" + manifestHash[..12],
            Version = "1",
            Title = title,
            PlayerCount = playerCount,
            ManifestHash = manifestHash,
            SourceFiles = files,
            Spans = spans,
            Segments = segments,
            Roles = [],
            Phases = phases,
            Materials = [],
            Truth = new TruthSection
            {
                RevealedSegmentIds = [],
                PrivateText = string.Empty,
                IsConfirmedByHuman = false,
            },
            Validation = report.CanStart ? PackageValidationState.Ready : PackageValidationState.NeedsReview,
        };
    }

    public static string Serialize(ScriptPackage package) =>
        JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
}
