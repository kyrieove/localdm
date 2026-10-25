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
    private readonly IEdgeTtsEngine _edge;
    private CancellationTokenSource _currentCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
    private bool _isPlayingEdge;
    private bool _disposed;

    public TtsService() : this(new SystemEdgeTtsEngine())
    {
    }

    /// <summary>可注入 Edge 引擎：降级路径的测试不依赖宿主机是否装了 python。</summary>
    public TtsService(IEdgeTtsEngine edge)
    {
        _edge = edge;
    }

    public IReadOnlyList<TtsVoice> GetChineseVoices()
    {
        var list = new List<TtsVoice>();
        if (_edge.IsAvailable())
        {
            list.Add(new TtsVoice { Name = "zh-CN-YunxiNeural", Culture = "zh-CN" });
            list.Add(new TtsVoice { Name = "zh-CN-YunjianNeural", Culture = "zh-CN" });
            list.Add(new TtsVoice { Name = "zh-CN-XiaoxiaoNeural", Culture = "zh-CN" });
        }

        var local = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .Where(v => v.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                        || v.Culture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Name.Contains("Kangkang", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Name.Contains("Yaoyao", StringComparison.OrdinalIgnoreCase))
            .Select(v => new TtsVoice { Name = v.Name, Culture = v.Culture.Name });

        list.AddRange(local);
        return list;
    }

    public bool IsSpeaking
    {
        get
        {
            lock (_stateLock)
            {
                return _synth.State == SynthesizerState.Speaking || _isPlayingEdge;
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
            _isPlayingEdge = false;
            _currentCts.Cancel();
            _edge.Stop();
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
                _isPlayingEdge = false;
                _edge.Stop();
                _synth.SpeakAsyncCancelAll();
            }

            try
            {
                // 如果未特别锁定本地声音，优先尝试 Edge-TTS 微软神经语音（默认 zh-CN-YunxiNeural 沉稳男声）
                var targetEdgeVoice = voiceId is not null && voiceId.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase)
                    ? voiceId
                    : "zh-CN-YunxiNeural";
                var edgeAllowed = (voiceId is null || voiceId.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase))
                    && _edge.IsAvailable();

                foreach (var chunk in SpeechChunks.Split(item.SafeText))
                {
                    linked.Token.ThrowIfCancellationRequested();

                    var usedEdge = false;
                    if (edgeAllowed)
                    {
                        try
                        {
                            lock (_stateLock) { _isPlayingEdge = true; }
                            usedEdge = await _edge.TrySpeakAsync(chunk, targetEdgeVoice, linked.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Edge 侧不可用（缺依赖、网络失败、进程起不来）：降级到本地 SAPI。
                            // 这里不把异常抛给界面：公共屏上不该出现 python 或路径细节。
                            LocalDm.Core.Diagnostics.RunLog.Write("Tts", "Edge-TTS 不可用，降级到 Windows 本地语音。");
                            usedEdge = false;
                        }
                        finally
                        {
                            lock (_stateLock) { _isPlayingEdge = false; }
                        }
                    }

                    if (!usedEdge)
                    {
                        // 一段失败后剩下的都用本地语音：来回换声更难听，也省得每段再等一次超时。
                        edgeAllowed = false;
                        // 降级前先确认没有被取消：Stop() 杀掉 Edge 进程后，TrySpeakAsync 可能以
                        // 非 0 退出码正常返回 false（没观察到取消），直接降级会把刚被主持人
                        // 停掉的文本又用本地语音念一遍。
                        linked.Token.ThrowIfCancellationRequested();
                        await SpeakLocalAsync(chunk, voiceId, linked.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    _edge.Stop();
                    _synth.SpeakAsyncCancelAll();
                }
                // 取消不改变 session；调用方负责丢弃晚到结果
                throw new DomainRuleException(DomainErrorCode.REQUEST_CANCELLED, "已停止播音。");
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private async Task SpeakLocalAsync(string text, string? voiceId, CancellationToken token)
    {
        // 降级使用 Windows System.Speech (默认 Microsoft Kangkang 沉稳男声)
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
            _synth.SpeakAsync(text);
            await tcs.Task.WaitAsync(token);
        }
        finally
        {
            _synth.SpeakCompleted -= OnCompleted;
        }
    }

    private void ApplyVoice(string? voiceId)
    {
        var sapiVoices = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .ToList();

        // 只有中文音色才进入选择链：本机若一个中文音色都没有，绝不能退到英文音色
        // 去念中文剧本（那会变成另一种「念错」）。
        var chineseVoices = sapiVoices
            .Where(v => v.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                        || v.Culture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 只有传入的名字属于本地 SAPI 声音时才直接使用；Edge-TTS 神经音色降级时自动选本地最优音色。
        // 匹配范围限定在中文音色里：settings.json 可以被手改，
        // 传进来一个英文音色名就会用英文音色念中文剧本。
        var matched = voiceId is not null ? chineseVoices.FirstOrDefault(v => v.Name == voiceId) : null;
        var chosen = matched?.Name
            ?? chineseVoices.FirstOrDefault(v => v.Name.Contains("Kangkang", StringComparison.OrdinalIgnoreCase))?.Name
            ?? chineseVoices.FirstOrDefault(v => v.Name.Contains("Yaoyao", StringComparison.OrdinalIgnoreCase))?.Name
            ?? chineseVoices.FirstOrDefault()?.Name;

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
            var fallback = chineseVoices.FirstOrDefault(v => v.Name != chosen)?.Name;
            if (fallback is null)
            {
                throw new DomainRuleException(
                    DomainErrorCode.TTS_UNAVAILABLE,
                    "本机没有可用的中文声音，文字已保留。");
            }

            try
            {
                _synth.SelectVoice(fallback);
            }
            catch
            {
                // 保持系统当前默认音色
            }
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
            _edge.Stop();
            _synth.SpeakAsyncCancelAll();
            _currentCts.Dispose();
        }

        _synth.Dispose();
        _queueLock.Dispose();
    }
}
