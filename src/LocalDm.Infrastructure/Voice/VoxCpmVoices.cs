using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// VoxCPM 音色的构造与展示名。
///
/// VoxCPM 的音色不是固定列表：音色设计由一段自然语言描述定义，
/// 音色克隆由一段参考音频定义。所以音色对象必须能由参数造出来，
/// 不能像 Edge 那样写死三个常量。
/// </summary>
public static class VoxCpmVoices
{
    /// <summary>由音色参数构造音色对象。</summary>
    public static TtsVoice Build(VoxCpmVoiceSpec spec) => new()
    {
        Name = BuildId(spec),
        Culture = "zh-CN",
        Engine = TtsEngineKind.VoxCpm,
    };

    /// <summary>
    /// 音色标识：<c>voxcpm:design:&lt;描述&gt;</c> 或 <c>voxcpm:clone:&lt;路径&gt;</c>。
    /// 这个标识会写进设置文件，重启后还要认得，所以格式不能随意改。
    /// </summary>
    public static string BuildId(VoxCpmVoiceSpec spec) => spec.Mode switch
    {
        VoxCpmVoiceSpecMode.Design => $"{TtsVoiceId.VoxCpmPrefix}{TtsVoiceId.DesignMarker}{spec.Description}",
        VoxCpmVoiceSpecMode.Clone => $"{TtsVoiceId.VoxCpmPrefix}{TtsVoiceId.CloneMarker}{spec.ReferenceAudioPath}",
        _ => throw new ArgumentOutOfRangeException(nameof(spec)),
    };

    /// <summary>
    /// 面向用户的中文名。下拉框宽度有限，名字要短。
    /// </summary>
    public static string DisplayName(VoxCpmVoiceSpec spec) => spec.Mode switch
    {
        VoxCpmVoiceSpecMode.Design => $"{spec.Description} · VoxCPM 本地",
        VoxCpmVoiceSpecMode.Clone => $"{Path.GetFileName(spec.ReferenceAudioPath ?? string.Empty)} · VoxCPM 克隆",
        _ => spec.Description ?? "VoxCPM",
    };

    /// <summary>完整说明，给悬停提示用。</summary>
    public static string Describe(VoxCpmVoiceSpec spec) => spec.Mode switch
    {
        VoxCpmVoiceSpecMode.Design => $"{spec.Description}（VoxCPM 本机合成 · 完全离线）",
        VoxCpmVoiceSpecMode.Clone => $"克隆自 {Path.GetFileName(spec.ReferenceAudioPath ?? string.Empty)}（VoxCPM 本机合成 · 完全离线）",
        _ => "VoxCPM",
    };
}
