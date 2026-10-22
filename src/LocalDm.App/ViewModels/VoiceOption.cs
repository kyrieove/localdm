using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 音色与它面向用户的中文名。
///
/// 展示文案属于界面层：领域侧只保留音色标识（Name/Culture），
/// 新增音色或调整文案都不必改 Core。
/// </summary>
public sealed record VoiceOption(TtsVoice Voice)
{
    /// <summary>音色标识，写进设置与 TTS 调用。</summary>
    public string Name => Voice.Name;

    /// <summary>下拉框里显示的中文名。</summary>
    public string DisplayName => TtsVoiceDisplay.For(Voice);

    /// <summary>完整说明，给悬停提示用。</summary>
    public string Description => TtsVoiceDisplay.Describe(Voice);
}

/// <summary>音色标识 → 面向用户的中文名。</summary>
public static class TtsVoiceDisplay
{
    /// <summary>
    /// 下拉框里显示的名字。要短：下拉框宽度有限，名字太长会被截断成「云希（微软神经 · 悬疑叙事」。
    /// </summary>
    public static string For(TtsVoice voice)
    {
        if (voice.Engine == TtsEngineKind.VoxCpm)
        {
            var spec = TtsVoiceId.ParseVoxCpm(voice.Name);
            return spec is null ? voice.Name : VoxCpmVoices.DisplayName(spec);
        }

        return For(voice.Name);
    }

    /// <summary>按标识显示的中文名。保留给不认识引擎的调用方。</summary>
    public static string For(string name) => name switch
    {
        "zh-CN-YunxiNeural" => "云希 · 微软神经男声",
        "zh-CN-YunjianNeural" => "云健 · 微软神经男声",
        "zh-CN-XiaoxiaoNeural" => "晓晓 · 微软神经女声",
        "Microsoft Kangkang" => "康康 · Windows 离线男声",
        "Microsoft Yaoyao" => "瑶瑶 · Windows 离线女声",
        "Microsoft Huihui Desktop" => "慧慧 · Windows 桌面语音",
        "Microsoft Huihui" => "慧慧 · Windows 内置语音",
        _ => name,
    };

    /// <summary>
    /// 完整说明（含气质描述），给悬停提示用。
    /// 气质描述对主持人选音色有用，但不该占下拉框的宽度。
    /// </summary>
    public static string Describe(TtsVoice voice)
    {
        if (voice.Engine == TtsEngineKind.VoxCpm)
        {
            var spec = TtsVoiceId.ParseVoxCpm(voice.Name);
            return spec is null ? voice.Name : VoxCpmVoices.Describe(spec);
        }

        return Describe(voice.Name);
    }

    /// <summary>按标识显示的完整说明。保留给不认识引擎的调用方。</summary>
    public static string Describe(string name) => name switch
    {
        "zh-CN-YunxiNeural" => "云希（微软神经 · 悬疑叙事男声）",
        "zh-CN-YunjianNeural" => "云健（微软神经 · 沉稳评书男声）",
        "zh-CN-XiaoxiaoNeural" => "晓晓（微软神经 · 真实自然女声）",
        "Microsoft Kangkang" => "康康（Windows 内置 · 沉稳离线男声）",
        "Microsoft Yaoyao" => "瑶瑶（Windows 内置 · 亲切离线女声）",
        "Microsoft Huihui Desktop" => "慧慧（Windows 桌面 · 经典机械音）",
        "Microsoft Huihui" => "慧慧（Windows 内置 · 经典机械音）",
        _ => name,
    };
}
