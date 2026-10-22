using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 微软 Edge 神经音色的引擎实现。
/// 实际合成走 <see cref="EdgeTtsEngine"/>（本机 python + edge-tts）。
/// </summary>
public sealed class EdgeTtsVoiceEngine : ITtsEngine
{
    /// <summary>Edge 提供的三个中文神经音色。顺序即展示顺序。</summary>
    private static readonly (string Name, string Culture)[] BuiltIn =
    [
        ("zh-CN-YunxiNeural", "zh-CN"),
        ("zh-CN-YunjianNeural", "zh-CN"),
        ("zh-CN-XiaoxiaoNeural", "zh-CN"),
    ];

    private readonly IEdgeTtsEngine _engine;

    public EdgeTtsVoiceEngine(IEdgeTtsEngine engine)
    {
        _engine = engine;
    }

    public TtsEngineKind Kind => TtsEngineKind.Edge;

    public bool IsAvailable() => _engine.IsAvailable();

    public IReadOnlyList<TtsVoice> GetVoices()
    {
        if (!_engine.IsAvailable())
        {
            return [];
        }

        return BuiltIn
            .Select(v => new TtsVoice { Name = v.Name, Culture = v.Culture, Engine = TtsEngineKind.Edge })
            .ToArray();
    }

    public Task<bool> TrySpeakAsync(string text, TtsVoice voice, CancellationToken cancellationToken) =>
        _engine.TrySpeakAsync(text, voice.Name, cancellationToken);

    public void Stop() => _engine.Stop();
}
