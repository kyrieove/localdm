namespace LocalDm.Core.Tablet;

/// <summary>
/// 平板发言提交后的即时处理结果。
/// </summary>
public enum TabletSpeechOutcome
{
    /// <summary>接受并开始处理一轮发言。</summary>
    Accepted,

    /// <summary>识别为环节或揭秘等推进提议，仅提示主持人确认，不送模型。</summary>
    Proposed,

    /// <summary>当前正在生成或朗读中，无法接受新发言。</summary>
    Busy,

    /// <summary>发言内容为空或仅包含空白字符。</summary>
    Empty,

    /// <summary>当前未加载剧本或未建立对局。</summary>
    NoSession,

    /// <summary>未配置模型 ID。</summary>
    NoModel,
}

/// <summary>
/// 平板发言提交确认：包含同步判定的结果，以及等待整轮处理（回答与朗读）完成的 Task。
/// </summary>
public sealed record TabletSpeechAck(TabletSpeechOutcome Outcome, Task Completion)
{
    /// <summary>构造一个直接被拒绝（Completion 已完成）的确认。</summary>
    public static TabletSpeechAck Rejected(TabletSpeechOutcome outcome) => new(outcome, Task.CompletedTask);
}

/// <summary>
/// 平板发言端口：供平板服务接入，向对局提交角色发言。
/// </summary>
public interface ITabletSpeech
{
    /// <summary>是否正处于生成回答或朗读中。</summary>
    bool IsBusy { get; }

    /// <summary>是否正在朗读语音。</summary>
    bool IsSpeaking { get; }

    /// <summary>提交平板角色发言。</summary>
    Task<TabletSpeechAck> SubmitAsync(string roleName, string text);

    /// <summary>打断当前的朗读。若正在朗读则打断并返回 true；否则返回 false。</summary>
    bool StopSpeaking();
}
