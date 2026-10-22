using LocalDm.Core.Ports;

namespace LocalDm.Infrastructure.Voice;

/// <summary>
/// 音色标识的格式与解析规则。
///
/// 标识会写进 <c>settings.json</c>，重启后还要认得，所以格式必须稳定。
/// 三种来源的标识互不冲突：
///
/// <list type="bullet">
/// <item>微软神经音色：<c>zh-CN-YunxiNeural</c>。历史格式，<b>不能改</b>：
/// 老用户的设置文件里存的就是它。</item>
/// <item>Windows 内置音色：<c>Microsoft Kangkang</c>。系统给出的原名。</item>
/// <item>VoxCPM 音色：<c>voxcpm:design:&lt;描述&gt;</c> 或
/// <c>voxcpm:clone:&lt;参考音频路径&gt;</c>。前缀加参数，参数本身要传给上游模型。</item>
/// </list>
///
/// 前缀是**词法边界**，不是「靠它猜引擎」：引擎归属由 <see cref="TtsVoice.Engine"/>
/// 明确给出，标识前缀只用来把 VoxCPM 的参数原样传回去。
/// </summary>
public static class TtsVoiceId
{
    /// <summary>VoxCPM 音色标识前缀。</summary>
    public const string VoxCpmPrefix = "voxcpm:";

    /// <summary>VoxCPM 音色设计：标识后接一段自然语言描述。</summary>
    public const string DesignMarker = "design:";

    /// <summary>VoxCPM 音色克隆：标识后接参考音频的绝对路径。</summary>
    public const string CloneMarker = "clone:";

    /// <summary>这个标识是不是 VoxCPM 音色。</summary>
    public static bool IsVoxCpm(string? voiceId) =>
        voiceId is not null && voiceId.StartsWith(VoxCpmPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 VoxCPM 音色标识。
    /// 不是 VoxCPM 标识、或格式不认识时返回 null，调用方据此报错而不是乱猜。
    /// </summary>
    public static VoxCpmVoiceSpec? ParseVoxCpm(string? voiceId)
    {
        if (!IsVoxCpm(voiceId))
        {
            return null;
        }

        var rest = voiceId![VoxCpmPrefix.Length..];

        if (rest.StartsWith(DesignMarker, StringComparison.OrdinalIgnoreCase))
        {
            var description = rest[DesignMarker.Length..].Trim();
            return description.Length == 0
                ? null
                : new VoxCpmVoiceSpec(VoxCpmVoiceSpecMode.Design, description, null);
        }

        if (rest.StartsWith(CloneMarker, StringComparison.OrdinalIgnoreCase))
        {
            var path = rest[CloneMarker.Length..].Trim();
            return path.Length == 0
                ? null
                : new VoxCpmVoiceSpec(VoxCpmVoiceSpecMode.Clone, null, path);
        }

        return null;
    }
}

/// <summary>VoxCPM 音色的两种来源。</summary>
public enum VoxCpmVoiceSpecMode
{
    /// <summary>音色设计：只用一段自然语言描述，不需要参考音频。</summary>
    Design = 0,

    /// <summary>音色克隆：拿一段参考音频复刻音色。</summary>
    Clone = 1,
}

/// <summary>
/// 一个 VoxCPM 音色的实际参数。
///
/// 对应上游 <c>VoxCPM.generate()</c> 的两种用法：
/// 音色设计把描述放在正文开头的圆括号里，音色克隆传 <c>reference_wav_path</c>。
/// </summary>
/// <param name="Mode">音色来源。</param>
/// <param name="Description">音色设计的自然语言描述。</param>
/// <param name="ReferenceAudioPath">音色克隆的参考音频绝对路径。</param>
public sealed record VoxCpmVoiceSpec(
    VoxCpmVoiceSpecMode Mode,
    string? Description,
    string? ReferenceAudioPath);
