using System.Runtime.Versioning;
using System.Speech.Synthesis;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// Windows System.Speech 实现：单队列、同一时刻最多一段播音。
/// Stop 取消当前与待播项；Replay 只复用已保存 safeText，不请求模型、不写状态。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TtsService : ITtsService, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource _currentCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
    private bool _disposed;

    public IReadOnlyList<TtsVoice> GetChineseVoices()
    {
        return _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .Where(v => v.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                        || v.Culture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            .Select(v => new TtsVoice { Name = v.Name, Culture = v.Culture.Name })
            .ToArray();
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_stateLock)
            {
                return _synth.State == SynthesizerState.Speaking;
            }
        }
    }

    public async Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
    {
        item.Validate();
        await PlayAsync(item, voiceId, cancellationToken);
    }

    public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
    {
        // 重播不生成事件、不调用模型、不重新提交操作
        item.Validate();
        return PlayAsync(item, voiceId, cancellationToken);
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _currentCts.Cancel();
            _synth.SpeakAsyncCancelAll();
        }
    }

    private async Task PlayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 串行化：同一时刻只有一个播音会话
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            CancellationTokenSource linked;
            lock (_stateLock)
            {
                _currentCts.Dispose();
                _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked = _currentCts;
                _synth.SpeakAsyncCancelAll();
            }

            try
            {
                ApplyVoice(voiceId);
                _synth.Rate = 0;
                _synth.Volume = 100;

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnCompleted(object? sender, SpeakCompletedEventArgs args)
                {
                    if (args.Cancelled)
                    {
                        tcs.TrySetCanceled();
                    }
                    else if (args.Error is not null)
                    {
                        tcs.TrySetException(new DomainRuleException(
                            DomainErrorCode.TTS_FAILED,
                            "播音失败，文字已保留。"));
                    }
                    else
                    {
                        tcs.TrySetResult();
                    }
                }

                _synth.SpeakCompleted += OnCompleted;
                try
                {
                    _synth.SpeakAsync(item.SafeText);
                    await tcs.Task.WaitAsync(linked.Token);
                }
                finally
                {
                    _synth.SpeakCompleted -= OnCompleted;
                }
            }
            catch (OperationCanceledException)
            {
                // 取消不改变 session；调用方负责丢弃晚到结果
                throw new DomainRuleException(DomainErrorCode.REQUEST_CANCELLED, "已停止播音。");
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private void ApplyVoice(string? voiceId)
    {
        var voices = GetChineseVoices();
        var chosen = voiceId is not null && voices.Any(v => v.Name == voiceId)
            ? voiceId
            : voices.FirstOrDefault()?.Name;

        if (chosen is null)
        {
            throw new DomainRuleException(
                DomainErrorCode.TTS_UNAVAILABLE,
                "本机没有可用的中文声音，文字已保留。");
        }

        try
        {
            _synth.SelectVoice(chosen);
        }
        catch (ArgumentException)
        {
            // 声音被系统移除：退回默认可枚举的中文音色
            var fallback = voices.FirstOrDefault(v => v.Name != chosen)?.Name;
            if (fallback is null)
            {
                throw new DomainRuleException(
                    DomainErrorCode.TTS_UNAVAILABLE,
                    "本机没有可用的中文声音，文字已保留。");
            }

            _synth.SelectVoice(fallback);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_stateLock)
        {
            _synth.SpeakAsyncCancelAll();
            _currentCts.Dispose();
        }

        _synth.Dispose();
        _queueLock.Dispose();
    }
}
