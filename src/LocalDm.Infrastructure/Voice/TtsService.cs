using System.Runtime.Versioning;
using System.Speech.Synthesis;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// Windows System.Speech 实现：单队列、同一时刻最多一段播音。
/// Stop 取消当前与待播项；Replay 只复用已保存 safeText，不请求模型、不写状态。
///
/// 引擎分派由 <see cref="TtsVoice.Engine"/> 决定，不靠音色名的前缀。
/// 降级链固定为：所选引擎 → Edge 神经音色 → Windows 内置音色。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TtsService : ITtsService, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly IEdgeTtsEngine _edge;
    private readonly ITtsEngine _edgeEngine;
    private readonly ITtsEngine? _voxcpm;
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
        _edgeEngine = new EdgeTtsVoiceEngine(edge);
    }

    /// <summary>可注入全部引擎：分派与降级路径的测试不依赖宿主机环境。</summary>
    public TtsService(IEdgeTtsEngine edge, ITtsEngine? voxcpm)
    {
        _edge = edge;
        _edgeEngine = new EdgeTtsVoiceEngine(edge);
        _voxcpm = voxcpm;
    }

    public IReadOnlyList<TtsVoice> GetChineseVoices()
    {
        var list = new List<TtsVoice>();

        // VoxCPM 音色排在最前：它完全在本机合成，口播内容不出本机。
        if (_voxcpm is not null)
        {
            list.AddRange(_voxcpm.GetVoices());
        }

        list.AddRange(_edgeEngine.GetVoices());

        var local = _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .Where(v => v.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                        || v.Culture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Name.Contains("Kangkang", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Name.Contains("Yaoyao", StringComparison.OrdinalIgnoreCase))
            .Select(v => new TtsVoice
            {
                Name = v.Name,
                Culture = v.Culture.Name,
                Engine = TtsEngineKind.Windows,
            });

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
            _voxcpm?.Stop();
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
                _voxcpm?.Stop();
                _synth.SpeakAsyncCancelAll();
            }

            try
            {
                // 第一级：主持人选的那个引擎。选不出引擎时留空。
                var route = ResolveRoute(voiceId);
                var usedEdge = false;

                if (route.Engine is not null && route.Voice is not null && route.Engine.IsAvailable())
                {
                    usedEdge = await TrySpeakWithEngineAsync(route.Engine, route.Voice, item.SafeText, linked.Token);
                }

                // 第二级：Edge 神经音色。
                // 只有「主持人没指定音色」或「所选的是 VoxCPM 且失败了」才走：
                // 主持人明确选了 Windows 内置音色时，不该被换成微软神经音色。
                if (!usedEdge && route.AllowEdgeFallback && _edgeEngine.IsAvailable())
                {
                    var edgeVoice = new TtsVoice
                    {
                        Name = "zh-CN-YunxiNeural",
                        Culture = "zh-CN",
                        Engine = TtsEngineKind.Edge,
                    };
                    usedEdge = await TrySpeakWithEngineAsync(_edgeEngine, edgeVoice, item.SafeText, linked.Token);
                }

                if (!usedEdge)
                {
                    // 降级前先确认没有被取消：Stop() 杀掉引擎进程后，TrySpeakAsync 可能以
                    // 非 0 退出码正常返回 false（没观察到取消），直接降级会把刚被主持人
                    // 停掉的文本又用本地语音念一遍。
                    linked.Token.ThrowIfCancellationRequested();

                    // 第三级：Windows System.Speech (默认 Microsoft Kangkang 沉稳男声)
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
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    _edge.Stop();
                    _voxcpm?.Stop();
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

    /// <summary>
    /// 调一个引擎朗读，失败一律吞掉并返回 false，让上层走降级。
    ///
    /// 引擎侧的异常（缺依赖、显存不足、进程起不来）不该出现在公共屏上：
    /// 那里只该有主持人的声音或文字，不该有 python 路径和显存报错。
    /// </summary>
    private async Task<bool> TrySpeakWithEngineAsync(
        ITtsEngine engine,
        TtsVoice voice,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_stateLock) { _isPlayingEdge = true; }
            return await engine.TrySpeakAsync(text, voice, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("[Tts] 引擎不可用，降级到下一个。");
            return false;
        }
        finally
        {
            lock (_stateLock) { _isPlayingEdge = false; }
        }
    }

    /// <summary>
    /// 一次朗读该走哪条路径。
    ///
    /// 把「用哪个引擎」与「失败后能不能退回 Edge」放在同一个值里，
    /// 是为了不让降级顺序散落成多处条件判断：
    /// 主持人明确选了 Windows 内置音色时，退回 Edge 等于擅自换掉他的选择。
    /// </summary>
    private readonly record struct VoiceRoute(
        ITtsEngine? Engine,
        TtsVoice? Voice,
        bool AllowEdgeFallback)
    {
        /// <summary>没指定音色：默认走 Edge，不可用再退回本地语音。</summary>
        public static VoiceRoute Default => new(null, null, true);

        /// <summary>主持人选的就是 Windows 内置音色：只用它，不换别的引擎。</summary>
        public static VoiceRoute WindowsOnly => new(null, null, false);

        /// <summary>主持人选了某个引擎的音色：失败后可以退回 Edge。</summary>
        public static VoiceRoute For(ITtsEngine? engine, TtsVoice voice) =>
            new(engine, voice, AllowsEdgeFallback(voice.Engine));
    }

    /// <summary>
    /// 某个引擎的音色失败后，能不能退回 Edge 神经音色。
    ///
    /// 单独暴露出来是为了让这条规则可以被直接测：
    /// 它决定「主持人明确选的音色会不会被悄悄换掉」。
    /// </summary>
    public static bool AllowsEdgeFallback(TtsEngineKind engine) =>
        engine is not TtsEngineKind.Windows;

    /// <summary>
    /// 按音色归属决定走哪条朗读路径。
    ///
    /// 归属来自 <see cref="TtsVoice.Engine"/>，不是从标识前缀猜的：
    /// 标识前缀只负责把 VoxCPM 的参数原样带回去。
    /// </summary>
    private VoiceRoute ResolveRoute(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            // 没指定音色：默认 Edge 神经音色，不可用再退回本地语音。
            return VoiceRoute.Default;
        }

        // 先在已注册的音色里找。Windows 内置音色的名字是系统给的，只能这样找。
        foreach (var voice in GetChineseVoices())
        {
            if (!string.Equals(voice.Name, voiceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return voice.Engine switch
            {
                TtsEngineKind.VoxCpm => VoiceRoute.For(_voxcpm, voice),
                // Edge 音色失败后直接退回本地语音，不必再试一次 Edge。
                TtsEngineKind.Edge => new VoiceRoute(_edgeEngine, voice, false),
                // Windows 内置音色：主持人明确选的就是它，不该被换成微软神经音色。
                _ => VoiceRoute.WindowsOnly,
            };        }

        // 不在列表里但确实是 VoxCPM 标识：按标识解析出来交给 VoxCPM 引擎。
        if (_voxcpm is not null && TtsVoiceId.IsVoxCpm(voiceId))
        {
            var spec = TtsVoiceId.ParseVoxCpm(voiceId);
            if (spec is not null)
            {
                return VoiceRoute.For(_voxcpm, VoxCpmVoices.Build(spec));
            }
        }

        // 认不出来的标识：按 Windows 内置音色处理，让 ApplyVoice 自己挑最接近的中文音色。
        return VoiceRoute.WindowsOnly;
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
            _voxcpm?.Stop();
            _synth.SpeakAsyncCancelAll();
            _currentCts.Dispose();
        }

        _synth.Dispose();
        _queueLock.Dispose();
    }
}
