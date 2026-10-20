using System.Collections.Concurrent;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Core.Hosting;

/// <summary>
/// 主持台统一入口：串联合成公开输入 → 模型文本 → TTS。
/// requestId 失效后丢弃晚到结果；同一时刻最多一段播音；Stop 同时取消 HTTP 与 TTS 队列。
/// </summary>
public sealed class HostDirector
{
    private readonly IMagpieGateway _gateway;
    private readonly ITtsService _tts;
    private readonly ConcurrentDictionary<string, byte> _liveRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<SpeechItem> _savedItems = [];
    private readonly object _stateLock = new();
    private CancellationTokenSource _requestCts = new();
    private SpeechState _state = SpeechState.Idle;

    public HostDirector(IMagpieGateway gateway, ITtsService tts)
    {
        _gateway = gateway;
        _tts = tts;
    }

    public SpeechState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>最近一次成功诵读的公开文字；重播只使用它。</summary>
    public SpeechItem? LastItem { get; private set; }

    /// <summary>
    /// 最近一次成功生成的 token 用量。失败或取消时保持上一次的值。
    /// 主持台据此判断上下文占用，不再靠猜字数。
    /// </summary>
    public MagpieUsage? LastUsage { get; private set; }

    /// <summary>一次请求的完整结果。Late 表示被取消后晚到、必须丢弃。</summary>
    public sealed record HostTurnResult(SpeechItem? Item, SpeechState State, string? PublicReason, bool Late);

    /// <summary>
    /// 只请求模型措辞，不播音。调用方先拿到文字，经公开政策核对后再决定播什么。
    /// </summary>
    public async Task<(string? Text, string? PublicReason, bool Late)> GenerateOnlyAsync(
        string modelId,
        IReadOnlyList<MagpieMessage> messages,
        CancellationToken cancellationToken = default)
    {
        SpeechRequestContext context;
        CancellationTokenSource linked;
        lock (_stateLock)
        {
            _requestCts.Dispose();
            _requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _requestCts;
            context = SpeechRequestContext.Create(sessionRevision: 0);
            _liveRequests[context.RequestId] = 0;
            _state = SpeechState.GeneratingText;
        }

        try
        {
            var completion = await _gateway.CompleteWithUsageAsync(
                new MagpieCompletionRequest { ModelId = modelId, Messages = messages },
                linked.Token);

            if (!IsLive(context.RequestId, linked))
            {
                return (null, "已取消，晚到的回复不会显示。", true);
            }

            // 用量只在真的拿到文字时更新：失败或取消不改动上一次的读数。
            LastUsage = completion.Usage;
            return (completion.Content, null, false);
        }
        catch (DomainRuleException ex)
        {
            SetState(SpeechState.Error);
            return (null, ex.PublicReason, false);
        }
        catch (OperationCanceledException)
        {
            SetState(SpeechState.Cancelled);
            return (null, "已取消。", false);
        }
        finally
        {
            _liveRequests.TryRemove(context.RequestId, out _);
        }
    }

    /// <summary>只把已通过公开核对的文字排入播音，不再请求模型。</summary>
    public async Task<HostTurnResult> SpeakApprovedAsync(
        string safeText,
        string? voiceId,
        CancellationToken cancellationToken = default)
    {
        var item = new SpeechItem
        {
            SpeechId = Guid.NewGuid().ToString("N"),
            RequestId = Guid.NewGuid().ToString("N"),
            SafeText = safeText,
            GenerationId = Guid.NewGuid().ToString("N"),
        };

        SetState(SpeechState.Speaking);
        try
        {
            await _tts.SpeakAsync(item, voiceId, cancellationToken);
            _savedItems.Add(item);
            LastItem = item;
            SetState(SpeechState.Idle);
            return new HostTurnResult(item, SpeechState.Idle, null, Late: false);
        }
        catch (DomainRuleException ex)
        {
            SetState(SpeechState.Error);
            return new HostTurnResult(null, SpeechState.Error, ex.PublicReason, Late: false);
        }
    }

    /// <summary>
    /// 用合成公开输入发起一次主持请求。
    /// </summary>
    public async Task<HostTurnResult> SpeakPublicAsync(
        string modelId,
        string publicInput,
        string? voiceId,
        CancellationToken cancellationToken = default)
    {
        SpeechRequestContext context;
        CancellationTokenSource linked;
        lock (_stateLock)
        {
            _requestCts.Dispose();
            _requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _requestCts;
            context = SpeechRequestContext.Create(sessionRevision: 0);
            _liveRequests[context.RequestId] = 0;
            _state = SpeechState.GeneratingText;
        }

        try
        {
            var text = await _gateway.CompleteAsync(
                new MagpieCompletionRequest
                {
                    ModelId = modelId,
                    Messages =
                    [
                        new MagpieMessage
                        {
                            Role = "system",
                            Content = "你是桌面剧本杀的主持人。只依据给出的公开内容作答，用中文，两句话以内。",
                        },
                        new MagpieMessage { Role = "user", Content = publicInput },
                    ],
                },
                linked.Token);

            if (!IsLive(context.RequestId, linked))
            {
                return new HostTurnResult(null, SpeechState.Cancelled, "已取消，晚到的回复不会显示。", Late: true);
            }

            var item = new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("N"),
                RequestId = context.RequestId,
                SafeText = text,
                GenerationId = context.GenerationId,
            };

            SetState(SpeechState.Speaking);
            var playing = _tts.SpeakAsync(item, voiceId, linked.Token);
            await playing;

            if (!IsLive(context.RequestId, linked))
            {
                return new HostTurnResult(null, SpeechState.Cancelled, "已取消播音。", Late: true);
            }

            _savedItems.Add(item);
            LastItem = item;
            SetState(SpeechState.Idle);
            return new HostTurnResult(item, SpeechState.Idle, null, Late: false);
        }
        catch (DomainRuleException ex) when (ex.Code == DomainErrorCode.REQUEST_CANCELLED)
        {
            SetState(SpeechState.Cancelled);
            return new HostTurnResult(null, SpeechState.Cancelled, ex.PublicReason, Late: false);
        }
        catch (DomainRuleException ex)
        {
            SetState(SpeechState.Error);
            return new HostTurnResult(null, SpeechState.Error, ex.PublicReason, Late: false);
        }
        catch (OperationCanceledException)
        {
            SetState(SpeechState.Cancelled);
            return new HostTurnResult(null, SpeechState.Cancelled, "已取消。", Late: false);
        }
        finally
        {
            _liveRequests.TryRemove(context.RequestId, out _);
        }
    }

    /// <summary>
    /// 停止：取消当前 HTTP token、使 requestId 失效、取消 TTS 队列。
    /// 不承诺上游停止计费或计算。
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            _requestCts.Cancel();
            _tts.Stop();
            _state = SpeechState.Cancelled;
        }
    }

    /// <summary>
    /// 重播：只播放已保存的公开口播，不请求模型、不写状态、不生成事件。
    /// </summary>
    public async Task<HostTurnResult> ReplayAsync(string? voiceId, CancellationToken cancellationToken = default)
    {
        var item = LastItem;
        if (item is null)
        {
            return new HostTurnResult(null, State, "还没有可重播的口播。", Late: false);
        }

        SetState(SpeechState.Speaking);
        try
        {
            await _tts.ReplayAsync(item, voiceId, cancellationToken);
            SetState(SpeechState.Idle);
            return new HostTurnResult(item, SpeechState.Idle, null, Late: false);
        }
        catch (DomainRuleException ex)
        {
            // 没有真的念出来就不算成功：Item 归 null，调用方据此显示失败而不是「已重播」。
            SetState(SpeechState.Error);
            return new HostTurnResult(null, SpeechState.Error, ex.PublicReason, Late: false);
        }
    }

    /// <summary>
    /// 重播某一条已生成的主持回复：只播放它保存下来的文字。
    /// 不再请求模型、不再提交任何状态，也不改动「最近一次口播」指针。
    ///
    /// 失败时返回 Item = null：调用方按「拿到 Item 才算念成功」判定，
    /// 否则界面会在没有声音的情况下报告「已重播」。
    /// </summary>
    public async Task<HostTurnResult> ReplayItemAsync(
        SpeechItem item,
        string? voiceId,
        CancellationToken cancellationToken = default)
    {
        item.Validate();
        SetState(SpeechState.Speaking);
        try
        {
            await _tts.ReplayAsync(item, voiceId, cancellationToken);
            SetState(SpeechState.Idle);
            return new HostTurnResult(item, SpeechState.Idle, null, Late: false);
        }
        catch (DomainRuleException ex)
        {
            var cancelled = ex.Code == DomainErrorCode.REQUEST_CANCELLED;
            SetState(cancelled ? SpeechState.Cancelled : SpeechState.Error);
            return new HostTurnResult(null, State, ex.PublicReason, Late: false);
        }
    }

    /// <summary>恢复后播音状态一律为 Idle，不补做任何未确认操作。</summary>
    public void ResetToIdle()
    {
        lock (_stateLock)
        {
            _state = SpeechState.Idle;
        }
    }

    private bool IsLive(string requestId, CancellationTokenSource cts) =>
        !cts.IsCancellationRequested && _liveRequests.ContainsKey(requestId);

    private void SetState(SpeechState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }
    }
}
