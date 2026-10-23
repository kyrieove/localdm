using System.Windows.Threading;
using LocalDm.App.ViewModels;
using LocalDm.Core.Game;
using LocalDm.Core.Tablet;

namespace LocalDm.App;

/// <summary>
/// 网页线程读公开消息、UI 线程做投影；消息和会话对不上就当还没装载，平板下一次轮询会拿到新的。
/// </summary>
public sealed class CurrentSessionTabletUiSource(IPublicFeedSource feed, Func<SessionViewModel> session, Dispatcher dispatcher) : ITabletUiSource
{
    public async Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken)
    {
        var snapshot = await feed.ReadAsync(after, cancellationToken);
        if (string.IsNullOrEmpty(snapshot.SessionId)) return null;

        // 包与会话要在 UI 线程上一起读：换局发生在 UI 线程，网页线程上分开读会读到新会话配旧包。
        return await dispatcher.InvokeAsync<TabletUiProjection?>(() =>
        {
            var view = session();
            if (view.Package is not { } package || view.Session is not { } current
                || !string.Equals(current.SessionId, snapshot.SessionId, StringComparison.Ordinal))
            {
                return null;
            }

            return TabletUiProjector.Project(package, current, snapshot, hostBusy, hostSpeaking);
        }).Task;
    }
}
