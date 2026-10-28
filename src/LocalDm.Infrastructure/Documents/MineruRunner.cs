using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Documents;

/// <summary>
/// MinerU 4.0.1 CLI 适配：不经 shell，使用 ArgumentList，异步消费 stdout/stderr。
/// PDF 显式 --pages all；图片不带 --pages；不加 --remote / --force。
/// 只解析白名单 read/parse 参数，不执行来自输出的命令字符串。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MineruRunner : IDocumentTool, IDisposable
{
    private readonly string _executable;
    private readonly string _appOwnedHome;
    private readonly int _timeoutSeconds;

    public MineruRunner(string executable, string appOwnedHome, int timeoutSeconds = 900)
    {
        _executable = executable;
        _appOwnedHome = appOwnedHome;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<ToolIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executable))
        {
            throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "未找到 MinerU 可执行文件。");
        }

        var (exitCode, stdout, stderr) = await RunAsync(["version"], cancellationToken);
        if (exitCode != 0)
        {
            throw new DomainRuleException(
                DomainErrorCode.TOOL_NOT_READY,
                "MinerU 版本检查失败：" + Diagnostics.RedactedLog.Redact(FirstLine(stderr)));
        }

        var match = System.Text.RegularExpressions.Regex.Match(stdout, @"版本:\s*([0-9]+\.[0-9]+\.[0-9]+)");
        var version = match.Success ? match.Groups[1].Value : "unknown";
        return new ToolIdentity
        {
            Tool = "mineru",
            Version = version,
            AppOwnedHome = _appOwnedHome,
        };
    }

    public async Task<DocumentExtraction> ParseAsync(
        DocumentParseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PrivacyDecision is PrivacyDecision.Blocked or PrivacyDecision.Unknown)
        {
            throw new DomainRuleException(
                DomainErrorCode.CONSENT_REQUIRED,
                "隐私预检未确认，未调用文档工具。");
        }

        var tier = TierArgument(request.Tier);
        var args = request.Kind == DocumentKind.Image
            ? new List<string> { "parse", request.AbsolutePath, "--tier", tier, "--json" }
            : ["parse", request.AbsolutePath, "--pages", "all", "--tier", tier, "--json"];

        return await ParseOnceAsync(request.FileId, args, cancellationToken);
    }

    private static string TierArgument(ParseTier tier) => tier switch
    {
        ParseTier.Flash => "flash",
        ParseTier.Basic => "basic",
        ParseTier.Standard => "standard",
        ParseTier.Advanced => "advanced",
        _ => throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "未知解析档位。"),
    };

    private async Task<DocumentExtraction> ParseOnceAsync(
        string fileId,
        List<string> args,
        CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunAsync([.. args], cancellationToken);
        var warnings = string.IsNullOrWhiteSpace(stderr)
            ? Array.Empty<string>()
            : [Diagnostics.RedactedLog.Redact(FirstLine(stderr))];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            // 退出码 0 不代表成功：没有可解析 JSON 就是失败
            throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "文档工具返回了非 JSON 输出。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                throw new DomainRuleException(
                    DomainErrorCode.TOOL_NOT_READY,
                    MapToolError(code));
            }

            var parse = root.TryGetProperty("parse", out var p) ? p : default;
            var status = parse.ValueKind == JsonValueKind.Object && parse.TryGetProperty("status", out var s)
                ? s.GetString()
                : null;

            ExtractionBatch[] batches = [];
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                batches = ReadBatches(content);
            }

            var next = ReadNextRequest(root, content);

            // 完成条件：parse.status=done、content 对象非 null、无 error、无待继续请求
            var complete = string.Equals(status, "done", StringComparison.Ordinal)
                           && root.TryGetProperty("content", out var c2)
                           && c2.ValueKind == JsonValueKind.Object
                           && next is null;

            if (exitCode != 0 && !complete)
            {
                throw new DomainRuleException(
                    DomainErrorCode.TOOL_NOT_READY,
                    "文档工具退出码非 0：" + (warnings.Length > 0 ? warnings[0] : "无输出"));
            }

            return new DocumentExtraction
            {
                FileId = fileId,
                ToolComplete = complete,
                Batches = batches,
                NextRequest = next,
                Warnings = warnings,
            };
        }
    }

    /// <summary>read 续读：只读 next_request.locator，用 read、locator、--json 构造参数。</summary>
    public async Task<DocumentExtraction> ReadAsync(
        string fileId,
        string locator,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormedLocator(locator))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "定位符格式不在白名单内。");
        }

        var args = new List<string> { "read", locator, "--json" };
        return await ParseOnceAsync(fileId, args, cancellationToken);
    }

    /// <summary>
    /// 读取只读状态（server / telemetry），供隐私预检使用；不执行其中任何命令字符串。
    /// </summary>
    public async Task<string> RunStatusAsync(string area, CancellationToken cancellationToken)
    {
        if (area is not ("server" or "telemetry"))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "状态查询只支持 server 与 telemetry。");
        }

        var args = area == "server"
            ? new[] { "server", "status", "--json" }
            : ["telemetry", "status", "--json"];
        var (_, stdout, _) = await RunAsync(args, cancellationToken);
        return stdout;
    }

    /// <summary>
    /// parse 续读：只用 page_range 与 after 构造白名单参数，不用 --force，不丢任务。
    /// </summary>
    public Task<DocumentExtraction> ParseContinuationAsync(
        DocumentParseRequest request,
        ContinuationRequest continuation,
        CancellationToken cancellationToken)
    {
        if (request.Kind == DocumentKind.Image)
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "图片不适用 parse 页续读。");
        }

        var args = new List<string> { "parse", request.AbsolutePath, "--pages", continuation.PageRange! };
        if (!string.IsNullOrWhiteSpace(continuation.After))
        {
            args.Add("--after");
            args.Add(continuation.After);
        }

        args.Add("--tier");
        args.Add(TierArgument(request.Tier));
        args.Add("--json");
        return ParseOnceAsync(request.FileId, args, cancellationToken);
    }

    private static bool IsWellFormedLocator(string locator) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            locator,
            @"^doc:[A-Za-z0-9_\-]+/tier:[A-Za-z0-9_\-]+/page:[0-9]+(/block:[0-9]+)?(/char:[0-9]+)?$");

    private static ExtractionBatch[] ReadBatches(JsonElement content)
    {
        // 4.0.1 实测两种返回形态：
        //   a) 结构化 pages[].blocks[]（块级，带 page_idx/index）
        //   b) 只有 content.content（markdown，带 <!-- page N of M --> 页标记）+ content_ranges 定位范围
        // content_ranges[].text 两种形态里都常常为空，只读它会导致「完成但没有来源」。
        var fromBlocks = ReadBlockBatches(content);
        if (fromBlocks.Count > 0)
        {
            return [.. fromBlocks];
        }

        var fromMarkdown = ReadMarkdownPageBatches(content);
        if (fromMarkdown.Count > 0)
        {
            return [.. fromMarkdown];
        }

        var list = new List<ExtractionBatch>();
        if (!content.TryGetProperty("content_ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var index = 0;
        foreach (var range in ranges.EnumerateArray())
        {
            // 没有 markdown 页标记时，只有 range 自带文本才算来源；空文本不生成页块。
            var text = range.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                index++;
                continue;
            }

            var pageRange = range.TryGetProperty("page_range", out var pr) ? pr.GetString() ?? string.Empty : string.Empty;

            var startLocator = ReadLocator(range, "start");
            var endLocator = ReadLocator(range, "end");
            var locator = startLocator
                ?? (range.TryGetProperty("locator", out var l) ? l.GetString() : null)
                ?? $"doc:unknown/tier:flash/page:{pageRange.Split('-')[0]}/block:{index}";

            list.Add(new ExtractionBatch
            {
                Locator = locator,
                Text = text,
                StartOffset = ReadOffset(range, "start"),
                EndOffset = ReadOffset(range, "end"),
                PageRange = pageRange,
                StartLocator = startLocator,
                EndLocator = endLocator,
            });
            index++;
        }

        return [.. list];
    }

    /// <summary>
    /// 从 content.pages[].blocks[] 生成来源：每个 block 一块，page_idx(0 基)+1 = 实际页号，
    /// locator 采用契约允许的 doc:ID/tier:TIER/page:N/block:N 形态。
    /// </summary>
    private static List<ExtractionBatch> ReadBlockBatches(JsonElement content)
    {
        var list = new List<ExtractionBatch>();
        if (!content.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var docId = content.TryGetProperty("short_id", out var sid) ? sid.GetString() : null;
        docId = string.IsNullOrWhiteSpace(docId) ? "doc" : docId;
        var tier = content.TryGetProperty("tier", out var tr) ? tr.GetString() : null;
        tier = string.IsNullOrWhiteSpace(tier) ? "flash" : tier;

        foreach (var page in pages.EnumerateArray())
        {
            var pageIdx = page.TryGetProperty("page_idx", out var pi) && pi.TryGetInt32(out var n) ? n : 0;
            var pageNumber = pageIdx + 1;
            if (!page.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in blocks.EnumerateArray())
            {
                var blockIndex = block.TryGetProperty("index", out var bi) && bi.TryGetInt32(out var b) ? b : 0;
                var text = ReadBlockText(block);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                list.Add(new ExtractionBatch
                {
                    Locator = $"doc:{docId}/tier:{tier}/page:{pageNumber}/block:{blockIndex}",
                    Text = text,
                    StartOffset = 0,
                    EndOffset = 0,
                    PageRange = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StartLocator = null,
                    EndLocator = null,
                });
            }
        }

        return list;
    }

    /// <summary>
    /// 从 markdown 正文的 page 标记切出每页文本；locator 取自 content_ranges 的页范围。
    /// 没有页标记时返回空，交由调用方判断（宁可报告缺失，也不猜页号）。
    /// </summary>
    private static List<ExtractionBatch> ReadMarkdownPageBatches(JsonElement content)
    {
        var list = new List<ExtractionBatch>();
        if (!content.TryGetProperty("content", out var body) || body.ValueKind != JsonValueKind.String)
        {
            return list;
        }

        var text = body.GetString() ?? string.Empty;
        var marker = System.Text.RegularExpressions.Regex.Matches(text, @"<!--\s*page\s+(?<n>[0-9]+)\s+of\s+(?<t>[0-9]+)\s*-->");
        if (marker.Count == 0)
        {
            return list;
        }

        var docId = "doc";
        var tier = content.TryGetProperty("tier", out var tr) ? tr.GetString() : null;
        tier = string.IsNullOrWhiteSpace(tier) ? "flash" : tier;
        if (content.TryGetProperty("content_ranges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
        {
            foreach (var range in ranges.EnumerateArray())
            {
                if (range.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.String
                    && start.GetString() is { } locator
                    && System.Text.RegularExpressions.Regex.Match(locator, @"^doc:(?<d>[^/]+)/tier:") is { Success: true } m)
                {
                    docId = m.Groups["d"].Value;
                    break;
                }
            }
        }

        for (var i = 0; i < marker.Count; i++)
        {
            var pageNumber = int.Parse(
                marker[i].Groups["n"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            var start = marker[i].Index + marker[i].Length;
            var end = i + 1 < marker.Count ? marker[i + 1].Index : text.Length;
            var pageText = text[start..end].Trim();
            if (pageText.Length == 0)
            {
                continue;
            }

            list.Add(new ExtractionBatch
            {
                Locator = $"doc:{docId}/tier:{tier}/page:{pageNumber}/block:0",
                Text = pageText,
                // 偏移是「页内」文本偏移，页起点起点处记为 0，跨页不累加
                StartOffset = 0,
                EndOffset = pageText.Length,
                PageRange = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StartLocator = $"doc:{docId}/tier:{tier}/page:{pageNumber}",
                EndLocator = null,
            });
        }

        return list;
    }

    /// <summary>拼接一个 block 的文本内容（block.content[] 里 may 有多个 text 片段）。</summary>
    private static string ReadBlockText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var pieces))
        {
            return block.TryGetProperty("text", out var direct) ? direct.GetString() ?? string.Empty : string.Empty;
        }

        if (pieces.ValueKind == JsonValueKind.String)
        {
            return pieces.GetString() ?? string.Empty;
        }

        if (pieces.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var piece in pieces.EnumerateArray())
        {
            if (piece.ValueKind == JsonValueKind.String)
            {
                parts.Add(piece.GetString() ?? string.Empty);
            }
            else if (piece.ValueKind == JsonValueKind.Object
                     && piece.TryGetProperty("content", out var inner)
                     && inner.ValueKind == JsonValueKind.String)
            {
                parts.Add(inner.GetString() ?? string.Empty);
            }
        }

        return string.Join('\n', parts);
    }

    /// <summary>start/end 既可能是数字偏移，也可能是 doc:ID/tier:TIER/page:N/block:N/char:OFFSET。</summary>
    private static string? ReadLocator(JsonElement range, string name) =>
        range.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadOffset(JsonElement range, string name)
    {
        if (!range.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when TryParseCharOffset(value.GetString()) is { } offset => offset,
            _ => 0,
        };
    }

    /// <summary>从 locator 尾部取 /char:OFFSET；没有字符偏移时返回 0（不是缺失）。</summary>
    private static int? TryParseCharOffset(string? locator)
    {
        if (string.IsNullOrEmpty(locator))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(locator, "/char:(?<n>[0-9]+)$");
        return match.Success
            ? int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    private static ContinuationRequest? ReadNextRequest(JsonElement root, JsonElement content)
    {
        // 优先 content.next_request；truncated=false 不能作为提前结束依据
        JsonElement next = default;
        var has = false;
        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("next_request", out var fromContent)
            && fromContent.ValueKind != JsonValueKind.Null)
        {
            next = fromContent;
            has = true;
        }
        else if (root.TryGetProperty("next_request", out var fromRoot) && fromRoot.ValueKind != JsonValueKind.Null)
        {
            next = fromRoot;
            has = true;
        }

        if (!has)
        {
            return null;
        }

        if (next.TryGetProperty("locator", out var locator))
        {
            var value = locator.GetString();
            return string.IsNullOrWhiteSpace(value)
                ? null
                : new ContinuationRequest { Kind = ContinuationKind.Read, Locator = value };
        }

        var kind = next.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (string.Equals(kind, "read", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ContinuationRequest
        {
            Kind = ContinuationKind.Parse,
            PageRange = next.TryGetProperty("page_range", out var pr) ? pr.GetString() : null,
            After = next.TryGetProperty("after", out var af) ? af.GetString() : null,
        };
    }

    private static string MapToolError(string? code) => code switch
    {
        "quality_tier_unavailable" => "该质量档位不可用：请改用已核验档位或导入已准备好的完整包。",
        "no_engine" => "本机没有可用解析引擎：请在应用专属 profile 中配置本地引擎。",
        "parse_server_unavailable" => "解析服务未运行：请先在应用专属 profile 中启动服务。",
        "remote_not_allowed" => "该调用需要远程处理但未获授权：不会上传真实文件。",
        null or "" => "文档工具返回错误。",
        _ => "文档工具错误：" + Diagnostics.RedactedLog.Redact(code),
    };

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // 子进程环境：清除继承的 MINERU_CONFIG 及其他 MINERU_* 覆盖，再设置应用自己的 MINERU_HOME
        var inherited = startInfo.Environment.Keys
            .Where(k => k.StartsWith("MINERU_", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in inherited)
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["MINERU_HOME"] = _appOwnedHome;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "无法启动文档工具进程。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时和用户取消都要结束进程树：否则取消导入后识别还在后台跑。
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new DomainRuleException(DomainErrorCode.TOOL_NOT_READY, "文档工具超时未返回。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    public void Dispose()
    {
    }
}
