namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// Edge-TTS 引擎的可替换契约。
///
/// 抽成接口是为了让「Edge 不可用时降级到 Windows 本地语音」这条路径
/// 能在没有 python、没有网络的机器上被真正测到 —— 产品的核心前提就是
/// 「不可用即优雅降级」，那么降级路径就不该依赖宿主机环境。
/// </summary>
public interface IEdgeTtsEngine
{
    /// <summary>神经音色是否可用。</summary>
    bool IsAvailable();

    /// <summary>尝试用神经音色朗读。成功返回 true；不可用或失败返回 false（调用方降级）。</summary>
    Task<bool> TrySpeakAsync(string text, string voice, CancellationToken cancellationToken);

    /// <summary>立即停止当前播音。</summary>
    void Stop();
}

/// <summary>默认实现：走本机 python + edge-tts 的真实引擎。</summary>
public sealed class SystemEdgeTtsEngine : IEdgeTtsEngine
{
    public bool IsAvailable() => EdgeTtsEngine.IsAvailable();

    public Task<bool> TrySpeakAsync(string text, string voice, CancellationToken cancellationToken) =>
        EdgeTtsEngine.TrySpeakAsync(text, voice, cancellationToken);

    public void Stop() => EdgeTtsEngine.Stop();
}
