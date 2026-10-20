using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Core.Ports;

namespace LocalDm.Tests;

/// <summary>
/// AI 起草流程：只验提示词与解析校验，模型是假的。
/// 测试断言只看结构与页号，不涉及任何剧本内容。
/// </summary>
public sealed class FlowDrafterTests
{
    private const string GoodJson =
        """
        {
          "layering": { "truthPages": [3], "ignoredPages": [1] },
          "steps": [
            { "stepId": "read", "title": "阅本", "kind": "Read",
              "hint": "拿到角色本，读到写着先不要翻页的地方为止。",
              "conditions": ["全员读完自己的部分"],
              "sourceRef": "组织者手册第 2 页", "offersOpeningSpeech": true },
            { "stepId": "search", "title": "搜证", "kind": "Search",
              "hint": "按编号翻线索卡。", "conditions": ["每处调查花一次时间"],
              "sourceRef": "组织者手册第 2 页" },
            { "stepId": "reveal", "title": "揭秘", "kind": "Reveal",
              "hint": "公开真相。", "conditions": [], "sourceRef": "组织者手册第 3 页" }
          ]
        }
        """;

    [Fact]
    public async Task Valid_json_becomes_a_flow_with_the_confirmed_organizer_group_and_no_public_pages()
    {
        var gateway = new ScriptedGateway(GoodJson);

        var result = await new FlowDrafter(gateway).DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var flow = result.Flow!;
        Assert.Equal("组织", flow.Layering!.OrganizerGroup);
        Assert.Empty(flow.Layering.PublicPages);
        Assert.Equal(new[] { 3 }, flow.Layering.TruthPages);
        Assert.Equal(3, flow.Steps.Count);
        Assert.Equal(ScriptStepKind.Read, flow.Steps[0].Kind);
        Assert.True(flow.Steps[0].OffersOpeningSpeech);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Public_pages_from_the_model_are_dropped()
    {
        var json = GoodJson.Replace(
            "\"truthPages\": [3]",
            "\"publicPages\": [2], \"truthPages\": [3]");

        var result = await new FlowDrafter(new ScriptedGateway(json))
            .DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Flow!.Layering!.PublicPages);
    }

    [Fact]
    public async Task Broken_json_is_retried_once_and_the_retry_carries_the_reason()
    {
        var gateway = new ScriptedGateway("这不是 JSON", GoodJson);

        var result = await new FlowDrafter(gateway).DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Contains("上一次的输出不能用", gateway.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("JSON 解析失败", gateway.Requests[1].Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_failures_report_a_failure_and_return_no_flow()
    {
        var gateway = new ScriptedGateway("还是不是 JSON", "也不是");

        var result = await new FlowDrafter(gateway).DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.Flow);
        Assert.Equal(2, result.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Theory]
    [InlineData("reveal-missing")]
    [InlineData("reveal-twice")]
    [InlineData("page-out-of-range")]
    public async Task Structurally_invalid_drafts_are_rejected(string variant)
    {
        var json = variant switch
        {
            // 没有 Reveal
            "reveal-missing" => GoodJson.Replace("\"kind\": \"Reveal\"", "\"kind\": \"Talk\""),
            // 两个 Reveal
            "reveal-twice" => GoodJson.Replace(
                "\"stepId\": \"search\", \"title\": \"搜证\", \"kind\": \"Search\"",
                "\"stepId\": \"search\", \"title\": \"搜证\", \"kind\": \"Reveal\""),
            // 真相页超出手册页数（共 3 页）
            _ => GoodJson.Replace("\"truthPages\": [3]", "\"truthPages\": [9]"),
        };

        var result = await new FlowDrafter(new ScriptedGateway(json, json))
            .DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.Flow);
    }

    [Fact]
    public async Task Chinese_kind_names_from_the_model_are_accepted()
    {
        // 提示词全是中文，模型常常直接答「阅本」「揭秘」：这两种写法都要认。
        var json = GoodJson
            .Replace("\"kind\": \"Read\"", "\"kind\": \"阅本\"")
            .Replace("\"kind\": \"Search\"", "\"kind\": \"搜证\"")
            .Replace("\"kind\": \"Reveal\"", "\"kind\": \"揭秘\"");

        var result = await new FlowDrafter(new ScriptedGateway(json))
            .DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ScriptStepKind.Read, result.Flow!.Steps[0].Kind);
        Assert.Equal(ScriptStepKind.Search, result.Flow.Steps[1].Kind);
        Assert.Equal(ScriptStepKind.Reveal, result.Flow.Steps[2].Kind);
    }

    [Fact]
    public async Task Unknown_kind_is_rejected_instead_of_guessed()
    {
        var json = GoodJson.Replace("\"kind\": \"Read\"", "\"kind\": \"随便写点什么\"");

        var result = await new FlowDrafter(new ScriptedGateway(json, json))
            .DraftAsync(Input(), "test-model", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("环节类别不认识", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_carries_every_manual_page_and_both_group_name_lists()
    {
        var input = Input();

        var messages = FlowDrafter.BuildMessages(input);

        Assert.Equal("system", messages[0].Role);
        Assert.Equal(FlowDrafter.Prompt, messages[0].Content);

        var user = messages[1].Content;
        foreach (var page in input.OrganizerPages)
        {
            Assert.Contains($"第 {page.PageNumber} 页", user, StringComparison.Ordinal);
            Assert.Contains(page.Text, user, StringComparison.Ordinal);
        }

        // 只给分组的名字，不给它们的任何文字。
        Assert.Contains("线索卡分组：书房", user, StringComparison.Ordinal);
        Assert.Contains("角色本分组：张三", user, StringComparison.Ordinal);
        Assert.Equal(input.OrganizerPages.Count, CountOccurrences(user, "第 "));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static FlowDraftInput Input() => new()
    {
        Title = "合成测试剧本",
        PlayerCount = 4,
        OrganizerGroupName = "组织",
        OrganizerPages =
        [
            new FlowDraftPage(1, "封面与版权页占位。"),
            new FlowDraftPage(2, "流程页占位：先读本，再搜证。"),
            new FlowDraftPage(3, "真相页占位。"),
        ],
        MaterialGroupNames = ["书房"],
        RoleGroupNames = ["张三"],
    };

    /// <summary>按顺序回固定内容的假网关；最后一个回复会用完一直重复。</summary>
    private sealed class ScriptedGateway : IMagpieGateway
    {
        private readonly string[] _replies;

        public ScriptedGateway(params string[] replies) => _replies = replies;

        public List<MagpieCompletionRequest> Requests { get; } = [];

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var reply = _replies[Math.Min(CallCount, _replies.Length - 1)];
            CallCount++;
            return Task.FromResult(reply);
        }
    }
}
