namespace LocalDm.Core.Tablet;

public interface ITabletUiSource
{
    /// <summary>取平板界面投影；本局还没装载（或消息所属局已换）时返回 null。</summary>
    Task<TabletUiProjection?> ReadAsync(long after, bool hostBusy, bool hostSpeaking, CancellationToken cancellationToken);
}
