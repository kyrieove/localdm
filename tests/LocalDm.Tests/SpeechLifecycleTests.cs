using System.Collections.Concurrent;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;

namespace LocalDm.Tests;

/// <summary>
/// T016：串行播放、取消队列、重播不生成状态事件。
/// 使用可观察的假 TTS；真实扬声器验证属于 US1 实机证据。
/// </summary>
public sealed class SpeechLifecycleTests
{
    [Fact]
    public async Task Two_speak_calls_are_serialized_never_overlapping()
    {
        var tts = new RecordingTts();
        var gateway = new ImmediateGateway("合成主持文字");
        var director = new HostDirector(gateway, tts);

        var first = director.SpeakPublicAsync("m", "合成输入一", null, TestContext.Current.CancellationToken);
        var second = director.SpeakPublicAsync("m", "合成输入二", null, TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second);

        Assert.Equal(1, tts.MaxConcurrent);
        Assert.Equal(2, tts.Played.Count);
    }

    [Fact]
    public async Task Stop_cancels_queue_without_committing_any_state()
    {
        var tts = new RecordingTts { BlockUntilCancelled = true };
        var gateway = new ImmediateGateway("合成主持文字");
        var director = new HostDirector(gateway, tts);

        var running = director.SpeakPublicAsync("m", "合成输入", null, TestContext.Current.CancellationToken);
        await tts.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        director.Stop();

        var result = await running;

        Assert.Equal(SpeechState.Cancelled, result.State);
        Assert.Null(result.Item);
        Assert.Equal(0, gateway.Commits);
        Assert.Equal(1, tts.StopCount);
    }

    [Fact]
    public async Task Late_reply_after_cancel_is_dropped_not_spoken()
    {
        var tts = new RecordingTts();
        var gate = new GatedGateway();
        var director = new HostDirector(gate, tts);

        var running = director.SpeakPublicAsync("m", "合成输入", null, TestContext.Current.CancellationToken);
        director.Stop();
        gate.Release("晚到的合成文字");

        var result = await running;

        Assert.True(result.Late);
        Assert.Empty(tts.Played);
        Assert.Equal(SpeechState.Cancelled, result.State);
    }

    [Fact]
    public async Task Replay_only_plays_saved_text_without_model_or_events()
    {
        var tts = new RecordingTts();
        var gateway = new ImmediateGateway("已保存的合成口播");
        var director = new HostDirector(gateway, tts);

        await director.SpeakPublicAsync("m", "合成输入", null, TestContext.Current.CancellationToken);
        var callsAfterSpeak = gateway.Calls;

        var replay = await director.ReplayAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(replay.Item);
        Assert.Equal("已保存的合成口播", replay.Item!.SafeText);
        Assert.Equal(callsAfterSpeak, gateway.Calls);
        Assert.Equal(2, tts.Played.Count);
        Assert.Equal(0, gateway.Commits);
    }

    [Fact]
    public async Task Tts_unavailable_keeps_text_and_reports_error()
    {
        var tts = new RecordingTts { FailureCode = DomainErrorCode.TTS_UNAVAILABLE };
        var director = new HostDirector(new ImmediateGateway("合成主持文字"), tts);

        var result = await director.SpeakPublicAsync("m", "合成输入", null, TestContext.Current.CancellationToken);

        Assert.Equal(SpeechState.Error, result.State);
        Assert.Contains("文字已保留", result.PublicReason!, StringComparison.Ordinal);
    }

    private sealed class ImmediateGateway : IMagpieGateway
    {
        private readonly string _reply;

        public ImmediateGateway(string reply) => _reply = reply;

        public int Calls { get; private set; }

        /// <summary>模型永远没有状态写权限：这里能「提交」的次数必须保持 0。</summary>
        public int Commits { get; private set; }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_reply);
        }
    }

    private sealed class GatedGateway : IMagpieGateway
    {
        private readonly TaskCompletionSource<string> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string text) => _gate.TrySetResult(text);

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);

        public async Task<string> CompleteAsync(
            MagpieCompletionRequest request,
            CancellationToken cancellationToken)
        {
            // 故意忽略 token：模拟上游在取消之后仍然返回结果
            return await _gate.Task;
        }
    }

    private sealed class RecordingTts : ITtsService
    {
        private readonly SemaphoreSlim _playLock = new(1, 1);
        private int _concurrent;

        public ConcurrentQueue<SpeechItem> PlayedItems { get; } = new();

        public IReadOnlyList<SpeechItem> Played => PlayedItems.ToArray();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockUntilCancelled { get; init; }

        public DomainErrorCode? FailureCode { get; init; }

        public int StopCount { get; private set; }

        public int MaxConcurrent { get; private set; }

        public bool IsSpeaking { get; private set; }

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "合成音色", Culture = "zh-CN" }];

        public async Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            await PlayCoreAsync(item, cancellationToken);
        }

        public async Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            await PlayCoreAsync(item, cancellationToken);
        }

        private async Task PlayCoreAsync(SpeechItem item, CancellationToken cancellationToken)
        {
            await _playLock.WaitAsync(cancellationToken);
            try
            {
                var now = Interlocked.Increment(ref _concurrent);
                MaxConcurrent = Math.Max(MaxConcurrent, now);
                IsSpeaking = true;
                Started.TrySetResult();
                if (FailureCode is { } code)
                {
                    throw new DomainRuleException(code, "本机没有可用的中文声音，文字已保留。");
                }

                if (BlockUntilCancelled)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                PlayedItems.Enqueue(item);
            }
            finally
            {
                IsSpeaking = false;
                Interlocked.Decrement(ref _concurrent);
                _playLock.Release();
            }
        }

        public void Stop()
        {
            StopCount++;
        }
    }
}
