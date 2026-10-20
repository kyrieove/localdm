using System.IO;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Tests;

/// <summary>
/// 备忘的护栏：整理请求写明「只写对话里出现过的事」，人工验收仍然出现过模型
/// 把还没发放的卡面细节写进备忘。这里检查两道：判据本身，以及主持台拿到这种备忘时整份不采用。
/// </summary>
public sealed class DmMemoCheckTests : IDisposable
{
    private const int Window = 10_000;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-memocheck-" + Guid.NewGuid().ToString("N"));

    public DmMemoCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    [Fact]
    public void Memo_stating_card_content_that_was_never_read_is_rejected()
    {
        // 线索卡原文里有一组四个字的说法，桌上没人读过它。
        const string NeverRead = "一把断掉的银钥匙静静地落在地毯上";
        var package = WithCardText(NeverRead);

        var offences = DmMemoCheck.UnsupportedTerms(
            "调查进度：大家觉得一把断掉的银钥匙是关键。",
            "玩家：我们去大厅看看。",
            package);

        Assert.NotEmpty(offences);
    }

    [Fact]
    public void Memo_that_only_repeats_what_was_said_on_the_table_passes()
    {
        var package = WithCardText("一把断掉的银钥匙静静地落在地毯上");
        const string Support = "玩家：我们去大厅看看挂着的那串钥匙。\nDM：好，先定下具体查哪一间。";

        var offences = DmMemoCheck.UnsupportedTerms(
            "调查进度：玩家商量过去大厅看挂着的那串钥匙，还没定下具体查哪一间。",
            Support,
            package);

        Assert.Empty(offences);
    }

    [Fact]
    public void Card_content_quoted_on_the_table_is_allowed()
    {
        const string NeverRead = "一把断掉的银钥匙静静地落在地毯上";
        var package = WithCardText(NeverRead);

        // 玩家逐字念了卡的第一行：念出来的这几个字已经上桌，可以写进备忘。
        var offences = DmMemoCheck.UnsupportedTerms(
            "线索：一把断掉的银钥匙。",
            "玩家：我念这张卡的第一行，一把断掉的银钥匙，剩下的你们自己看。",
            package);

        Assert.Empty(offences);
    }

    /// <summary>线索卡原文里放一段没人读过的卡面文字。判据只看材料分组指向的 span，所以材料和段一起换。</summary>
    private static ScriptPackage WithCardText(string cardText)
    {
        var package = SyntheticPackage.Create();
        return package with
        {
            Spans = package.Spans
                .Select(span => span.SpanId == "s7" ? span with { OriginalText = cardText } : span)
                .ToArray(),
            Materials = package.Materials
                .Select(material => material with { SourceSpanIds = ["s7"] })
                .ToArray(),
            Segments = package.Segments
                .Select(segment => segment.SegmentId == "seg-clue"
                    ? segment with { OriginalText = cardText, SourceSpanIds = ["s7"] }
                    : segment)
                .ToArray(),
        };
    }

    [Fact]
    public void Organizer_manual_wording_is_not_policed()
    {
        // 主持人本来就可以带出手册里的流程与规则，那不是玩家还没读到的东西。
        var package = SyntheticPackage.Create();

        var offences = DmMemoCheck.UnsupportedTerms(
            "规则：阅本之后进入搜证，投票环节要纸笔汇总。",
            "玩家：现在干什么？",
            package);

        Assert.Empty(offences);
    }

    [Fact]
    public async Task Unsourced_memo_is_not_adopted_and_the_turn_still_goes_out()
    {
        // 演示剧本里也有 s7 这个线索卡 span：卡面换成一段桌上没人读过的文字。
        var package = WithCardText("一把断掉的银钥匙静静地落在地毯上");
        var gateway = new ScriptedGateway
        {
            // 模型把没读过的卡面细节写了进去：这份备忘不许采用。
            CompactionMemo = "本局备忘：一把断掉的银钥匙，剩下一半大家自己看。",
        };
        var host = await NewHostAsync(gateway, package);
        await host.LoadPackageAsync(package);

        for (var i = 0; i < 40 && host.Conversation.CompactionCount == 0; i++)
        {
            host.InputText = $"第 {i} 轮：" + new string('问', 400);
            await host.SendAsync();
            await WaitIdleAsync(host);
        }

        Assert.Equal(0, host.Conversation.CompactionCount);
        Assert.Null(host.Conversation.Memo);
        Assert.Contains(
            host.Messages,
            m => m.IsEvent && m.Text.Contains("这份备忘没有采用", StringComparison.Ordinal));
        Assert.True(gateway.Requests.Count(r => !IsCompaction(r)) > 3, "这一轮和之后的对话都要照常发出。");
    }

    [Fact]
    public async Task Sourced_memo_is_adopted()
    {
        var gateway = new ScriptedGateway { CompactionMemo = "本局备忘：大家商量过去大厅看钥匙。" };
        var host = await NewHostAsync(gateway);
        await host.LoadDemoAsync();

        for (var i = 0; i < 40 && host.Conversation.CompactionCount == 0; i++)
        {
            host.InputText = $"第 {i} 轮：" + new string('问', 400);
            await host.SendAsync();
            await WaitIdleAsync(host);
        }

        Assert.Equal(1, host.Conversation.CompactionCount);
        Assert.Contains("大厅", host.Conversation.Memo, StringComparison.Ordinal);
    }

    private static bool IsCompaction(MagpieCompletionRequest request) =>
        request.Messages.Any(m => m.Content.Contains("【整理本局记忆】", StringComparison.Ordinal));

    private async Task<HostViewModel> NewHostAsync(IMagpieGateway gateway, ScriptPackage? package = null)
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(CancellationToken.None);
        var host = new HostViewModel(
            new HostDirector(gateway, new SilentTts()),
            () => new ToolSettings
            {
                ModelId = "test-model",
                MagpieBaseUri = new Uri("http://127.0.0.1:1"),
                ContextWindowTokens = Window,
            },
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            new SilentTts());
        ((ScriptedGateway)gateway).Attach(host);
        return host;
    }

    private static async Task WaitIdleAsync(HostViewModel host)
    {
        for (var i = 0; i < 200 && (host.IsBusy || host.Session.Busy); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>可控的假网关：整理请求返回指定的备忘，其余照常。</summary>
    private sealed class ScriptedGateway : IMagpieGateway
    {
        public List<MagpieCompletionRequest> Requests { get; } = [];

        public string CompactionMemo { get; set; } = "本局备忘：已经查过书房。";

        private HostViewModel? _host;

        public void Attach(HostViewModel host) => _host = host;

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ReplyFor(request));

        public Task<MagpieCompletion> CompleteWithUsageAsync(
            MagpieCompletionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var usage = new MagpieUsage(
                ContextBudget.EstimateTokens(string.Join("\n", request.Messages.Select(m => m.Content))),
                20,
                null);
            _ = _host;
            return Task.FromResult(new MagpieCompletion(ReplyFor(request), usage));
        }

        private string ReplyFor(MagpieCompletionRequest request) =>
            IsCompaction(request) ? CompactionMemo : "这是测试用的公开主持文字。";
    }

    private sealed class SilentTts : ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }
}



















