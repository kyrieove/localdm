using LocalDm.Core.Domain;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// VoxCPM 引擎运行所需的配置。
///
/// 这是一份**快照**：设置页改了模型目录或参考音频后，组合根造一份新的给引擎，
/// 引擎不需要自己去读设置文件。这样引擎的依赖是显式的，也便于测试注入。
/// </summary>
/// <param name="ModelPath">模型目录或 HuggingFace 仓库名。空表示不启用 VoxCPM。</param>
/// <param name="PythonPath">Python 解释器路径。空表示用环境变量或 PATH 上的 python。</param>
/// <param name="VoiceDesign">音色设计的自然语言描述。空表示不启用该音色。</param>
/// <param name="ReferenceAudioPath">克隆用的参考音频路径。空表示不启用该音色。</param>
public sealed record VoxCpmOptions(
    string? ModelPath,
    string? PythonPath,
    string? VoiceDesign,
    string? ReferenceAudioPath)
{
    /// <summary>没有配置模型目录时不启用 VoxCPM。</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(ModelPath);

    /// <summary>从设置里取 VoxCPM 配置。</summary>
    public static VoxCpmOptions FromSettings(ToolSettings settings) => new(
        settings.VoxCpmModelPath,
        settings.VoxCpmPythonPath,
        settings.VoxCpmVoiceDesign,
        settings.VoxCpmReferenceAudio);

    /// <summary>
    /// 这个配置下有哪些 VoxCPM 音色。
    /// 音色设计与克隆是两种独立来源，各自可有可无。
    /// </summary>
    public IReadOnlyList<VoxCpmVoiceSpec> GetVoiceSpecs()
    {
        var specs = new List<VoxCpmVoiceSpec>();

        if (!string.IsNullOrWhiteSpace(VoiceDesign))
        {
            specs.Add(new VoxCpmVoiceSpec(VoxCpmVoiceSpecMode.Design, VoiceDesign.Trim(), null));
        }

        if (!string.IsNullOrWhiteSpace(ReferenceAudioPath))
        {
            specs.Add(new VoxCpmVoiceSpec(VoxCpmVoiceSpecMode.Clone, null, ReferenceAudioPath.Trim()));
        }

        return specs;
    }
}
