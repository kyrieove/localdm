using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 一个 TTS 引擎的可替换契约。
///
/// 抽成接口是为了让「某引擎不可用时降级到下一个」这条路径
/// 能在没有 python、没有网络的机器上被真正测到 —— 产品的核心前提就是
/// 「不可用即优雅降级」，那么降级路径就不该依赖宿主机环境。
///
/// 引擎自己负责回答两件事：能不能用，以及自己提供哪些音色。
/// 分派由 <see cref="TtsVoice.Engine"/> 决定，引擎不猜音色名的前缀。
/// </summary>
public interface ITtsEngine
{
    /// <summary>引擎标识，与 <see cref="TtsVoice.Engine"/> 对应。</summary>
    TtsEngineKind Kind { get; }

    /// <summary>该引擎是否可用（依赖齐备、进程能起）。不可用时上层降级。</summary>
    bool IsAvailable();

    /// <summary>该引擎提供的音色。不可用时返回空列表，不抛异常。</summary>
    IReadOnlyList<TtsVoice> GetVoices();

    /// <summary>
    /// 尝试用本引擎朗读。成功返回 true；不可用或失败返回 false（调用方降级）。
    /// </summary>
    Task<bool> TrySpeakAsync(string text, TtsVoice voice, CancellationToken cancellationToken);

    /// <summary>立即停止当前播音。</summary>
    void Stop();
}
