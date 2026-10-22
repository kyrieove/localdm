using LocalDm.Core.Tablet;

namespace LocalDm.Tablet;

/// <summary>
/// 未准备好的原因，给人看的中文说明。
/// 平板上要能一眼看出「这一组暂停分发，用纸质卡」，而不是一个 404 裂图。
/// </summary>
public static class TabletNotReadyText
{
    public static string Describe(TabletNotReadyReason? reason) => reason switch
    {
        TabletNotReadyReason.MissingFront => "这一组少正面，暂停分发",
        TabletNotReadyReason.PairingConflict => "正反面配不唯一，暂停分发",
        TabletNotReadyReason.CropUnconfirmed => "切片还没确认，暂停分发",
        TabletNotReadyReason.AssetMissing => "资产缺失，暂停分发",
        _ => "这一组暂停分发",
    };
}
