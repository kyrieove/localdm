using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 独立运行的平板服务看不到桌面那一局，所以没有界面可给。
/// </summary>
public sealed class EmptyTabletUiSource : ITabletUiSource
{
    public Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken) =>
        Task.FromResult<TabletUiProjection?>(null);
}
