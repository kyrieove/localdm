namespace LocalDm.Core.Ports;

public enum DictationState { Idle, Preparing, Listening, Recognizing }

/// <summary>识别结果：Text 为空时 Problem 说明原因，直接给人看。</summary>
public sealed record DictationResult(string Text, string? Problem);

/// <summary>
/// 按下说话转文字：本机录音、本机识别，音频只在内存里，识别完即丢，不落盘。
/// </summary>
public interface IDictationService : IDisposable
{
    DictationState State { get; }

    event Action<DictationState>? StateChanged;

    /// <summary>准备模型时的进度文字（只在首次复制模型时出现）。</summary>
    event Action<string>? Progress;

    /// <summary>准备模型并开始录音。返回 null 表示已经在听；返回文字表示没能开始，原因直接给人看。</summary>
    Task<string?> StartAsync(CancellationToken cancellationToken);

    /// <summary>停止录音并识别。</summary>
    Task<DictationResult> StopAsync(CancellationToken cancellationToken);

    /// <summary>放弃本次录音，什么都不识别。</summary>
    void Cancel();
}
