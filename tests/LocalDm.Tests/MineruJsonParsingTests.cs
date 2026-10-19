using System.Text.Json;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Documents;

namespace LocalDm.Tests;

/// <summary>
/// 回归：MinerU 4.0.1 的 CLI JSON 里 content_ranges[].text 通常为空、start/end 是 locator 字符串，
/// 正文在 content.content（带 &lt;!-- page N of M --&gt; 页标记）。
/// 曾因只读 content_ranges 导致 ToolComplete=true 但 spans=0（成功却没有任何来源）。
/// </summary>
public sealed class MineruJsonParsingTests
{
    [Fact]
    public void Page_markers_split_markdown_into_page_sourced_batches()
    {
        const string json = """
            {
              "parse": { "status": "done", "tier": "flash", "page_range": "1-2" },
              "content": {
                "short_id": "abc123",
                "tier": "flash",
                "content": "<!-- page 1 of 2 -->\n\n第一页正文\n\n<!-- page 2 of 2 -->\n\n第二页正文",
                "content_ranges": [
                  { "page_range": "1-2", "start": "doc:abc123/tier:flash/page:1", "end": "doc:abc123/tier:flash/page:2/block:3" }
                ],
                "truncated": false,
                "next_request": null
              }
            }
            """;

        var batches = InvokeReadBatches(json);

        Assert.Equal(2, batches.Count);
        Assert.Contains(batches, b => b.Text.Contains("第一页正文", StringComparison.Ordinal));
        Assert.Contains(batches, b => b.Text.Contains("第二页正文", StringComparison.Ordinal));
        Assert.All(batches, b => Assert.StartsWith("doc:abc123/tier:flash/page:", b.Locator, StringComparison.Ordinal));
        Assert.Equal(["1", "2"], batches.Select(b => b.PageRange));
    }

    [Fact]
    public void String_start_and_end_do_not_throw_and_keep_original_locators()
    {
        const string json = """
            {
              "parse": { "status": "done" },
              "content": {
                "tier": "flash",
                "content": "<!-- page 1 of 1 -->\n\n正文",
                "content_ranges": [
                  { "page_range": "1", "start": "doc:d1/tier:flash/page:1", "end": "doc:d1/tier:flash/page:1/char:42" }
                ]
              }
            }
            """;

        var batches = InvokeReadBatches(json);

        var batch = Assert.Single(batches);
        Assert.Equal("doc:d1/tier:flash/page:1", batch.StartLocator);
        // 页内偏移：起点 0、长度等于该页文本长度（跨页不累加）
        Assert.Equal(0, batch.StartOffset);
        Assert.Equal(batch.Text.Length, batch.EndOffset);
    }

    [Fact]
    public void Blocks_are_used_when_the_payload_has_structured_pages()
    {
        const string json = """
            {
              "parse": { "status": "done" },
              "content": {
                "tier": "flash",
                "pages": [
                  { "page_idx": 0, "blocks": [
                      { "index": 0, "type": "text", "content": [ { "type": "text", "content": "块一" } ] },
                      { "index": 1, "type": "text", "content": [ { "type": "text", "content": "块二" } ] }
                  ] }
                ],
                "content_ranges": []
              }
            }
            """;

        var batches = InvokeReadBatches(json);

        Assert.Equal(2, batches.Count);
        Assert.Equal("doc:doc/tier:flash/page:1/block:0", batches[0].Locator);
        Assert.Equal("块一", batches[0].Text);
        Assert.Equal("doc:doc/tier:flash/page:1/block:1", batches[1].Locator);
    }

    [Fact]
    public void Payload_without_page_markers_yields_no_source_rather_than_guessing()
    {
        const string json = """
            {
              "parse": { "status": "done" },
              "content": { "tier": "flash", "content": "没有页标记的正文", "content_ranges": [] }
            }
            """;

        var batches = InvokeReadBatches(json);

        Assert.Empty(batches);
    }

    [Fact]
    public void Parse_tier_is_explicit_in_arguments()
    {
        // tier 必须由调用方指定；这里只校验枚举映射，不启动真实进程。
        Assert.Equal(0, (int)ParseTier.Flash);
        Assert.True(Enum.IsDefined(ParseTier.Standard));
    }

    private static IReadOnlyList<ExtractionBatch> InvokeReadBatches(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("content");
        var method = typeof(MineruRunner).GetMethod(
            "ReadBatches",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyList<ExtractionBatch>)method.Invoke(null, [content])!;
    }
}
