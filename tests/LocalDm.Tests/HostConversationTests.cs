using System.IO;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// 对话链路：玩家消息、主持回复与阶段事件按时间顺序落在同一条线上；
/// 自动朗读、停止与逐条重播都不会重复改变游戏状态。
/// </summary>
public sealed class HostConversationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-chat-" + Guid.NewGuid().ToString("N"));

    public HostConversationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Player_message_and_host_reply_share_one_timeline_and_auto_read_speaks()
    {
        var gateway = new CountingGateway();
        var tts = new CountingTts();
        var host = await NewHostAsync(gateway, tts, autoRead: true);
        await host.LoadDemoAsync();

        var before = host.Messages.Count;
        host.InputText = "现在该做什么？";
        await host.SendAsync();

        // 玩家原话与主持回复接在同一条对话线上，顺序不变
        Assert.Equal(before + 2, host.Messages.Count);
        var player = host.Messages[before];
        Assert.True(player.IsPlayer);
        Assert.Equal("现在该做什么？", player.Text);

        var reply = host.Messages[before + 1];
        Assert.True(reply.IsHost);
        Assert.Equal(CountingGateway.Reply, reply.Text);

        // 回复生成后自动朗读：生成一次模型请求、朗读一次
        Assert.Equal(1, gateway.CompletionCount);
        Assert.Equal(1, tts.SpeakCount);
        Assert.Equal(ChatSpeechState.Spoken, reply.SpeechState);
        Assert.Contains("已朗读", reply.SpeechNote, StringComparison.Ordinal);
        Assert.True(reply.CanReplay);
        Assert.False(host.IsBusy);
    }

    [Fact]
    public async Task Auto_read_off_keeps_the_text_and_replay_does_not_call_the_model()
    {
        var gateway = new CountingGateway();
        var tts = new CountingTts();
        var host = await NewHostAsync(gateway, tts, autoRead: false);
        await host.LoadDemoAsync();

        host.InputText = "再讲一次规则。";
        await host.SendAsync();

        var reply = host.Messages[^1];
        Assert.True(reply.IsHost);
        Assert.Equal(ChatSpeechState.Ready, reply.SpeechState);
        Assert.Contains("自动朗读已关闭", reply.SpeechNote, StringComparison.Ordinal);
        Assert.Equal(0, tts.SpeakCount);
        Assert.Equal(1, gateway.CompletionCount);

        // 重播只用已保存的文字：不再请求模型，也不再提交任何状态
        var revisionBefore = host.Session.Revision;
        reply.ReplayCommand!.Execute(null);
        await WaitUntilAsync(() => tts.ReplayCount == 1);

        Assert.Equal(1, gateway.CompletionCount);
        Assert.Equal(revisionBefore, host.Session.Revision);
        Assert.Equal(ChatSpeechState.Spoken, reply.SpeechState);
        Assert.Contains("未请求模型", reply.SpeechNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_discards_the_late_reply_and_keeps_the_progress()
    {
        var gateway = new CountingGateway { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var tts = new CountingTts();
        var host = await NewHostAsync(gateway, tts, autoRead: true);
        await host.LoadDemoAsync();

        var revisionBefore = host.Session.Revision;
        host.InputText = "开始之前先说点什么。";
        var sending = host.SendAsync();

        Assert.True(host.IsBusy, "等待模型时应处于忙碌状态。");
        Assert.True(host.ShowStop, "忙碌时停止按钮应当出现。");

        host.StopCommand.Execute(null);
        Assert.False(host.IsBusy);
        Assert.False(host.ShowStop);
        Assert.Contains(host.Messages, m => m.IsEvent && m.Text.Contains("已停止本次回应", StringComparison.Ordinal));

        // 晚到的回复必须被丢弃
        gateway.Gate!.SetResult();
        await sending;

        Assert.DoesNotContain(host.Messages, m => m.IsHost && m.Text == CountingGateway.Reply);
        Assert.Equal(0, tts.SpeakCount);
        Assert.Equal(revisionBefore, host.Session.Revision);
    }

    [Fact]
    public async Task Chat_that_asks_to_advance_does_not_change_the_stage()
    {
        var gateway = new CountingGateway();
        var tts = new CountingTts();
        var host = await NewHostAsync(gateway, tts, autoRead: true);
        await host.LoadDemoAsync();
        await host.Session.ExecuteAsync(GameCommandType.Start);
        await host.Session.ExecuteAsync(GameCommandType.Advance);
        // 阶段提交后的播报是事件驱动的：等它收尾再发聊天，避免抢在忙碌闸上
        await WaitUntilAsync(() => !host.IsBusy);

        var phaseBefore = host.Session.PhaseTitle;
        var revisionBefore = host.Session.Revision;
        var modelCallsBefore = gateway.CompletionCount;

        host.InputText = "进入下一环节";
        await host.SendAsync();

        Assert.Equal(phaseBefore, host.Session.PhaseTitle);
        Assert.Equal(revisionBefore, host.Session.Revision);
        Assert.Equal(modelCallsBefore + 1, gateway.CompletionCount);
        var reply = host.Messages[^1];
        Assert.True(reply.IsHost);
        Assert.Equal(CountingGateway.Reply, reply.Text);
    }

    [Fact]
    public async Task Follow_up_includes_previous_player_and_model_turns_once()
    {
        var gateway = new CountingGateway();
        var host = await NewHostAsync(gateway, new CountingTts(), autoRead: false);
        await host.LoadDemoAsync();
        host.InputText = "请介绍故事背景，不要复述时长。";
        await host.SendAsync();
        host.InputText = "接着刚才的背景展开讲。";
        await host.SendAsync();

        var messages = gateway.Requests[^1].Messages;
        Assert.Contains(messages, m => m.Role == "user" && m.Content == "请介绍故事背景，不要复述时长。");
        Assert.Contains(messages, m => m.Role == "assistant" && m.Content == CountingGateway.Reply);
        Assert.Single(messages, m => m.Role == "user" && m.Content.Contains("接着刚才的背景展开讲。", StringComparison.Ordinal));
        // model ID 属于当前局面：它在最后一条 user 消息里，不在 system 里。
        Assert.Contains(messages, m => m.Role == "user" && m.Content.Contains("test-model", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Role == "system" && m.Content.Contains("test-model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_script_keeps_previous_players_out_of_the_new_conversation()
    {
        var gateway = new CountingGateway();
        var host = await NewHostAsync(gateway, new CountingTts(), autoRead: false);
        await host.LoadDemoAsync();
        host.InputText = "上一局玩家的独有提问。";
        await host.SendAsync();

        var other = LocalDm.App.Demo.DemoPackage.Create() with { PackageId = "other-script", Title = "另一份合成剧本" };
        await host.LoadPackageAsync(other);
        host.InputText = "我们现在开始另一局。";
        await host.SendAsync();

        Assert.DoesNotContain(host.Messages, m => m.Text == "上一局玩家的独有提问。");
        Assert.DoesNotContain(gateway.Requests[^1].Messages, m => m.Content.Contains("上一局玩家的独有提问。", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replay_holds_the_session_busy_gate_so_the_stage_cannot_move()
    {
        var gateway = new CountingGateway();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tts = new CountingTts { ReplayGate = gate };
        var host = await NewHostAsync(gateway, tts, autoRead: true);
        await host.LoadDemoAsync();
        await host.Session.ExecuteAsync(GameCommandType.Start);
        await WaitUntilAsync(() => !host.IsBusy);

        var revisionBefore = host.Session.Revision;
        Assert.True(host.Session.CanAdvance, "阅本环节本来可以推进，用它来验证重播期间的闸门。");

        host.InputText = "先说说这一局怎么走。";
        await host.SendAsync();
        var reply = host.Messages[^1];
        Assert.True(reply.CanReplay);

        // 重播挂在半途：宿主忙，会话也必须忙，否则旧音频还在播就能推进阶段
        reply.ReplayCommand!.Execute(null);
        await WaitUntilAsync(() => tts.ReplayStarted);
        Assert.True(host.IsBusy, "重播期间宿主应处于忙碌。");
        Assert.True(host.Session.Busy, "重播期间会话也必须占住播报闸。");
        Assert.False(host.Session.CanAdvance, "重播期间不能推进阶段。");

        // 真的去点：必须被拒绝，revision 不变
        await host.Session.ExecuteAsync(GameCommandType.Advance);
        Assert.Equal(revisionBefore, host.Session.Revision);
        Assert.Contains("还在进行", host.Session.Status, StringComparison.Ordinal);

        gate.SetResult();
        await WaitUntilAsync(() => !host.IsBusy);
        Assert.False(host.Session.Busy);
        Assert.Equal(revisionBefore, host.Session.Revision);
        Assert.Equal(ChatSpeechState.Spoken, reply.SpeechState);
    }

    [Fact]
    public async Task Failed_replay_reports_failure_and_keeps_the_text()
    {
        var gateway = new CountingGateway();
        var tts = new CountingTts { ReplayError = DomainErrorCode.TTS_UNAVAILABLE };
        var host = await NewHostAsync(gateway, tts, autoRead: true);
        await host.LoadDemoAsync();

        host.InputText = "再讲一次规则。";
        await host.SendAsync();
        var reply = host.Messages[^1];
        Assert.Equal(CountingGateway.Reply, reply.Text);

        reply.ReplayCommand!.Execute(null);
        await WaitUntilAsync(() => tts.ReplayStarted && !host.IsBusy);

        // 没有声音就不能报「已重播」：必须是失败，而且文字还在
        Assert.Equal(ChatSpeechState.Failed, reply.SpeechState);
        Assert.Contains("失败", reply.SpeechNote, StringComparison.Ordinal);
        Assert.Equal(CountingGateway.Reply, reply.Text);
        Assert.True(host.Activity.IsError, "失败必须以错误样式留在状态行上。");
        Assert.False(host.Session.Busy, "失败后要把会话闸放回去。");
    }

    [Fact]
    public async Task Opening_speech_button_follows_the_flow_declaration()
    {
        var gateway = new CountingGateway();
        var tts = new CountingTts();
        var host = await NewHostAsync(gateway, tts, autoRead: true);

        var store = new SessionStore(Path.Combine(_root, "sessions-op.sqlite"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var package = OpeningSpeechPackage();
        await store.SaveSessionAsync(
            new Session
            {
                SessionId = "session-opening",
                PackageId = package.PackageId,
                PackageVersion = package.Version,
                ManifestHash = package.ManifestHash,
                State = SessionState.Prepared,
                CurrentPhaseId = package.Phases[0].PhaseId,
                Revision = 0,
                ReleasedMaterialIds = [],
            },
            TestContext.Current.CancellationToken);
        await host.LoadPackageAsync(package);

        await host.Session.ExecuteAsync(GameCommandType.Start);
        await WaitUntilAsync(() => !host.IsBusy);
        Assert.Equal("read", host.Session.Projection!.CurrentPhase.PhaseId);
        Assert.False(host.ShowOpeningSpeech, "第一步没有声明开场动作，就不该出现这个按钮。");

        await host.Session.ExecuteAsync(GameCommandType.Advance);
        await WaitUntilAsync(() => !host.IsBusy);
        Assert.Equal("prologue", host.Session.Projection!.CurrentPhase.PhaseId);
        Assert.True(host.ShowOpeningSpeech, "第二步声明了开场动作，按钮应出现在这里。");
    }

    /// <summary>第二步（序章）才是「开场」所在环节：用来说明按钮不由环节序号决定。</summary>
    private static ScriptPackage OpeningSpeechPackage()
    {
        var pages = new[]
        {
            new LocalDm.Core.Import.RealScriptPackageBuilder.ExtractedPage
            {
                Group = "组织", PageNumber = 1, PageLabel = "1", Text = "公共序章",
                FileId = "o0", SpanId = "os0",
            },
        };
        var sources = pages.Select(p => new SourceFile
        {
            FileId = p.FileId,
            PrivatePath = p.FileId,
            ContentHash = new string('a', 64),
            PageCount = 1,
            Origin = SourceOrder.ManifestOrder,
            ToolIdentity = "test",
            ParseState = FileParseState.Complete,
        }).ToArray();

        var flow = new ScriptFlow
        {
            Steps =
            [
                new ScriptFlowStep
                {
                    StepId = "read", Title = "阅本", Kind = ScriptStepKind.Read,
                    SourceRef = "第 1 页", Conditions = ["读完自己的本"],
                },
                new ScriptFlowStep
                {
                    StepId = "prologue", Title = "序章共读", Kind = ScriptStepKind.Intro,
                    SourceRef = "第 2 页", OffersOpeningSpeech = true,
                },
                new ScriptFlowStep
                {
                    StepId = "ending", Title = "结局", Kind = ScriptStepKind.Reveal,
                    SourceRef = "第 9 页",
                },
            ],
        };

        return LocalDm.Core.Import.RealScriptPackageBuilder
            .Build("开场声明合成剧本", 5, pages, sources, flow: flow)
            .Package;
    }

    private async Task<HostViewModel> NewHostAsync(CountingGateway gateway, CountingTts tts, bool autoRead)
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var settings = ToolSettings.Default with { ModelId = "test-model", AutoRead = autoRead };
        return new HostViewModel(
            new HostDirector(gateway, tts),
            () => settings,
            new HostIntentRouter(),
            new CommandDispatcher(store),
            store,
            tts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private sealed class CountingGateway : IMagpieGateway
    {
        public List<MagpieCompletionRequest> Requests { get; } = [];
        public const string Reply = "这是测试用的公开主持文字。";

        public int CompletionCount { get; private set; }

        /// <summary>挡板：设了它就等它完成才返回，用来模拟「还在生成」。</summary>
        public TaskCompletionSource? Gate { get; init; }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public async Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CompletionCount++;
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Reply;
        }
    }

    private sealed class CountingTts : ITtsService
    {
        public int SpeakCount { get; private set; }

        public int ReplayCount { get; private set; }

        /// <summary>挡板：设了它，重播就一直停在「正在朗读」，用来模拟长音频。</summary>
        public TaskCompletionSource? ReplayGate { get; init; }

        /// <summary>设了它，重播直接失败（例如本机没有可用中文声音）。</summary>
        public DomainErrorCode? ReplayError { get; init; }

        public bool ReplayStarted => ReplayCount > 0;

        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            SpeakCount++;
            return Task.CompletedTask;
        }

        public async Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            ReplayCount++;
            if (ReplayError is { } code)
            {
                throw new DomainRuleException(code, "本机没有可用的中文声音，文字已保留。");
            }

            if (ReplayGate is not null)
            {
                await ReplayGate.Task;
            }
        }

        public void Stop()
        {
        }
    }
}
