using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 独立探针的演示发言宿主：接受任何发言，之后 3 秒内视为主持忙。
/// 只用于独立探针，不代表真实主持。
/// </summary>
public sealed class SpikeTabletSpeech(TimeProvider? time = null) : ITabletSpeech
{
    private static readonly TimeSpan BusyFor = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _busyUntil = DateTimeOffset.MinValue;

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _time.GetUtcNow() < _busyUntil;
            }
        }
    }

    public bool IsSpeaking => false;

    public Task<TabletSpeechAck> SubmitAsync(string roleName, string text)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now < _busyUntil)
            {
                return Task.FromResult(TabletSpeechAck.Rejected(TabletSpeechOutcome.Busy));
            }

            _busyUntil = now + BusyFor;
        }

        return Task.FromResult(new TabletSpeechAck(TabletSpeechOutcome.Accepted, Task.Delay(BusyFor)));
    }

    public bool StopSpeaking() => false;
}
