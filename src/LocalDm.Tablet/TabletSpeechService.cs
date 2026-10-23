using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 平板发言返回码。
/// </summary>
public enum TabletSayCode
{
    NoSessionKey,
    NoRole,
    Empty,
    TooLong,
    Busy,
    TooFast,
    NotReady,
    Accepted,
    Proposed,
}

/// <summary>
/// 平板发言判定结果。
/// </summary>
public sealed record TabletSayResult(TabletSayCode Code, int HttpStatus, string CodeText, string Message, Task Completion);

/// <summary>
/// 平板发言规则与转发服务。
/// 将会话 Cookie 键与发言文本做合法性、频次及忙碌检查，转给 ITabletSpeech。
/// </summary>
public sealed class TabletSpeechService(TabletSpikeState state, ITabletSpeech speech, TimeProvider time)
{
    // shortcut: 按 UTF-16 单元计数，表情符号算 2，真要精确再改 StringInfo
    public const int MaxLength = 300;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool HostBusy => speech.IsBusy;
    public bool HostSpeaking => speech.IsSpeaking;

    /// <summary>
    /// 校验发言规则并提交给主持宿主。
    /// </summary>
    public async Task<TabletSayResult> SayAsync(string? sessionKey, string? text)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return new TabletSayResult(TabletSayCode.NoSessionKey, 401, "NO_SESSION_KEY", "请先从电脑上的二维码或网址重新打开本页。", Task.CompletedTask);
        }

        var roleId = state.Session(sessionKey).RoleId;
        if (string.IsNullOrEmpty(roleId))
        {
            return new TabletSayResult(TabletSayCode.NoRole, 403, "NO_ROLE", "请先认领角色", Task.CompletedTask);
        }

        var role = state.Roles.FirstOrDefault(r => string.Equals(r.RoleId, roleId, StringComparison.Ordinal));
        if (role is null)
        {
            return new TabletSayResult(TabletSayCode.NoRole, 403, "NO_ROLE", "请先认领角色", Task.CompletedTask);
        }

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new TabletSayResult(TabletSayCode.Empty, 400, "EMPTY", "说点什么再发送。", Task.CompletedTask);
        }

        if (trimmed.Length > MaxLength)
        {
            return new TabletSayResult(TabletSayCode.TooLong, 400, "TOO_LONG", "单条最多 300 字。", Task.CompletedTask);
        }

        if (speech.IsBusy)
        {
            return new TabletSayResult(TabletSayCode.Busy, 409, "BUSY", "主持正在回应", Task.CompletedTask);
        }

        DateTimeOffset? previous = null;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_last.TryGetValue(sessionKey, out var prev))
            {
                if (now - prev < MinInterval)
                {
                    return new TabletSayResult(TabletSayCode.TooFast, 429, "TOO_FAST", "发得太快了，请稍等一下。", Task.CompletedTask);
                }

                previous = prev;
            }

            _last[sessionKey] = now;
        }

        TabletSpeechAck ack;
        try
        {
            ack = await speech.SubmitAsync(role.DisplayName, trimmed);
        }
        catch
        {
            lock (_gate)
            {
                if (previous.HasValue)
                {
                    _last[sessionKey] = previous.Value;
                }
                else
                {
                    _last.Remove(sessionKey);
                }
            }

            throw;
        }

        if (ack.Outcome is not (TabletSpeechOutcome.Accepted or TabletSpeechOutcome.Proposed))
        {
            lock (_gate)
            {
                if (previous.HasValue)
                {
                    _last[sessionKey] = previous.Value;
                }
                else
                {
                    _last.Remove(sessionKey);
                }
            }
        }

        return ack.Outcome switch
        {
            TabletSpeechOutcome.Accepted => new TabletSayResult(TabletSayCode.Accepted, 202, "ACCEPTED", "已发送", ack.Completion),
            TabletSpeechOutcome.Proposed => new TabletSayResult(TabletSayCode.Proposed, 202, "PROPOSED", "已提议，等电脑确认", ack.Completion),
            TabletSpeechOutcome.Busy => new TabletSayResult(TabletSayCode.Busy, 409, "BUSY", "主持正在回应", Task.CompletedTask),
            TabletSpeechOutcome.Empty => new TabletSayResult(TabletSayCode.Empty, 400, "EMPTY", "说点什么再发送。", Task.CompletedTask),
            TabletSpeechOutcome.NoSession => new TabletSayResult(TabletSayCode.NotReady, 409, "NOT_READY", "电脑还没有开始本局。", Task.CompletedTask),
            TabletSpeechOutcome.NoModel => new TabletSayResult(TabletSayCode.NotReady, 409, "NOT_READY", "电脑还没有选好主持模型。", Task.CompletedTask),
            _ => new TabletSayResult(TabletSayCode.NotReady, 409, "NOT_READY", "电脑还没有开始本局。", Task.CompletedTask),
        };
    }

    /// <summary>
    /// 打断主持朗读。
    /// </summary>
    public bool Stop(string? sessionKey, out TabletSayCode failure)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            failure = TabletSayCode.NoSessionKey;
            return false;
        }

        var roleId = state.Session(sessionKey).RoleId;
        if (string.IsNullOrEmpty(roleId) || !state.Roles.Any(r => string.Equals(r.RoleId, roleId, StringComparison.Ordinal)))
        {
            failure = TabletSayCode.NoRole;
            return false;
        }

        failure = TabletSayCode.Accepted;
        return speech.StopSpeaking();
    }
}

/// <summary>
/// 未配置发言宿主时的默认空实现。
/// </summary>
internal sealed class NoTabletSpeech : ITabletSpeech
{
    public bool IsBusy => false;
    public bool IsSpeaking => false;

    public Task<TabletSpeechAck> SubmitAsync(string roleName, string text) =>
        Task.FromResult(TabletSpeechAck.Rejected(TabletSpeechOutcome.NoSession));

    public bool StopSpeaking() => false;
}
