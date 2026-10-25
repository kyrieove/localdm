using LocalDm.Core.Ports;
using LocalDm.Core.Tablet;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 公开消息的写入队列。
///
/// 桌上每落一条消息就排一条进来，按顺序写进存档。
/// 顺序不能乱、序号不能跳：平板按序号补发，跳号就意味着漏消息。
///
/// 写失败立刻停住：不再往下写，把原因报给界面（同步异常），
/// 没写成功的那条留在队列里，等重试。序号由存储层在事务里分配，
/// 失败的那次不会消耗序号，所以重来一次仍然是连续的。
///
/// 不能吞掉错误，也不能对外报告「已同步」。
/// </summary>
public sealed class PublicMessagePublisher
{
    private readonly IPublicMessageFeed _feed;
    private readonly object _gate = new();
    private readonly List<PublicMessage> _pending = [];
    private bool _running;
    private bool _failed;
    private bool _recovering;
    private string? _failure;

    public PublicMessagePublisher(IPublicMessageFeed feed)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
    }

    /// <summary>写失败时触发：界面据此显示同步异常。</summary>
    public event EventHandler<string>? Failed;

    /// <summary>重试成功、队列清空时触发。</summary>
    public event EventHandler? Recovered;

    /// <summary>一条消息真的落盘之后触发：网页据此通知平板刷新。</summary>
    public event EventHandler<PublicMessage>? Committed;

    /// <summary>同步是不是已经断了。</summary>
    public bool HasFailure
    {
        get
        {
            lock (_gate)
            {
                return _failed;
            }
        }
    }

    /// <summary>同步断掉的原因，可以直接展示给主持人。</summary>
    public string? FailureReason
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>还有多少条没写进去。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>排一条公开消息。写盘是异步的，不挡界面。</summary>
    public void Publish(PublicMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _pending.Add(message);
        }

        EnsureFlushing();
    }

    /// <summary>重试：把没写成功的重新写一遍。</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (!_failed)
            {
                return;
            }

            _failed = false;
            _failure = null;
            _recovering = true;
        }

        EnsureFlushing();
    }

    private void EnsureFlushing()
    {
        lock (_gate)
        {
            // 只允许一个写手：两个写手会同时取同一条消息，顺序就乱了。
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = FlushAsync();
    }

    private async Task FlushAsync()
    {
        try
        {
            while (true)
            {
                PublicMessage? next = null;
                var recovered = false;
                lock (_gate)
                {
                    if (!_failed && _pending.Count > 0)
                    {
                        next = _pending[0];
                    }
                    else
                    {
                        _running = false;
                        if (_pending.Count == 0 && _recovering)
                        {
                            _recovering = false;
                            recovered = true;
                        }
                    }
                }

                // 事件在锁外触发：处理方可能回调进来。
                if (recovered)
                {
                    Recovered?.Invoke(this, EventArgs.Empty);
                }

                if (next is null)
                {
                    return;
                }

                PublicMessage stored;
                try
                {
                    stored = await _feed.AppendPublicMessageAsync(next, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var reason = ex is PublicMessageSyncException sync
                        ? sync.PublicReason
                        : "公开消息没有存下来。";
                    lock (_gate)
                    {
                        _failed = true;
                        _failure = reason;
                        _running = false;
                    }

                    Failed?.Invoke(this, reason);
                    return;
                }

                lock (_gate)
                {
                    _pending.RemoveAt(0);
                }

                Committed?.Invoke(this, stored);
            }
        }
        catch (Exception ex)
        {
            // 兜底：后台异常不能变成未处理异常把整个应用带走。
            lock (_gate)
            {
                _running = false;
            }

            LocalDm.Core.Diagnostics.RunLog.Write("Public", "写公开消息时出了意外：" + LocalDm.Infrastructure.Diagnostics.RedactedLog.Redact(Convert.ToString(ex.Message)));
        }
    }
}
