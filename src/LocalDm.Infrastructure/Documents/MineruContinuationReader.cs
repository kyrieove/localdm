using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;

namespace LocalDm.Infrastructure.Import;

/// <summary>
/// 续读器：按 parse 的 page_range/after 或 read 的 locator 续读至 next_request=null。
/// 保存每批 ranges，检测循环继续请求与遗漏页；next_request 非空时不能因 truncated=false 结束。
/// </summary>
public sealed class MineruContinuationReader
{
    private readonly MineruRunner _runner;
    private readonly int _maxIterations;

    public MineruContinuationReader(MineruRunner runner, int maxIterations = 64)
    {
        _runner = runner;
        _maxIterations = maxIterations;
    }

    public async Task<DocumentExtraction> ExtractToCompletionAsync(
        DocumentParseRequest request,
        CancellationToken cancellationToken)
    {
        var batches = new List<ExtractionBatch>();
        var seenRequests = new HashSet<string>(StringComparer.Ordinal);

        var extraction = await _runner.ParseAsync(request, cancellationToken);
        batches.AddRange(extraction.Batches);

        var steps = 0;
        while (extraction.NextRequest is { } next)
        {
            steps++;
            if (steps > _maxIterations)
            {
                throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "续读次数超出上限，已停止。");
            }

            var key = next.Kind == ContinuationKind.Read
                ? "read:" + next.Locator
                : $"parse:{next.PageRange}:{next.After}";
            if (!seenRequests.Add(key))
            {
                // 相同的继续请求再次出现 = 工具在循环，不能无限重试
                throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "检测到重复的继续请求，已停止。");
            }

            extraction = next.Kind == ContinuationKind.Read
                ? await _runner.ReadAsync(request.FileId, next.Locator!, cancellationToken)
                : await ReadParseContinuationAsync(request, next, cancellationToken);

            batches.AddRange(extraction.Batches);
        }

        return new DocumentExtraction
        {
            FileId = request.FileId,
            ToolComplete = extraction.ToolComplete,
            Batches = batches,
            NextRequest = null,
            Warnings = extraction.Warnings,
        };
    }

    private Task<DocumentExtraction> ReadParseContinuationAsync(
        DocumentParseRequest request,
        ContinuationRequest next,
        CancellationToken cancellationToken)
    {
        // parse 续读只能读 page_range 与 after 并构造白名单参数；不执行自由 shell 字符串
        if (string.IsNullOrWhiteSpace(next.PageRange))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "继续请求缺少 page_range。");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(next.PageRange, @"^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$"))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "继续请求的 page_range 不在白名单内。");
        }

        return _runner.ParseContinuationAsync(request, next, cancellationToken);
    }
}
