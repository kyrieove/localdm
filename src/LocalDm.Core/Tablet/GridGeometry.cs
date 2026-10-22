namespace LocalDm.Core.Tablet;

/// <summary>
/// 网格几何：把「行数、列数、有效区域」换算成每一格在整张原图上的像素矩形。
///
/// 每条边都按整图位置独立取整：格子的右边界是 <c>x0 + (列数 × 宽) / 总列数</c> 直接取整，
/// 不是「上一格的右边界 + 每格宽度」。逐格累加在边长除不尽时会越切越偏，
/// 切到最后一列就少一条边或多出一条邻卡。
/// 这里不碰文件、不认任何剧本的尺寸，只做几何。
/// </summary>
public static class GridGeometry
{
    /// <summary>这一格在整图上的像素矩形。行列越界时返回 null。</summary>
    public static PixelRegion? SlotRect(GridLayout layout, GridSlot slot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (slot.Row < 0 || slot.Row >= layout.Rows || slot.Column < 0 || slot.Column >= layout.Columns)
        {
            return null;
        }

        var region = layout.Region;
        var left = region.X + Scale(slot.Column, region.Width, layout.Columns);
        var right = region.X + Scale(slot.Column + 1, region.Width, layout.Columns);
        var top = region.Y + Scale(slot.Row, region.Height, layout.Rows);
        var bottom = region.Y + Scale(slot.Row + 1, region.Height, layout.Rows);

        return new PixelRegion(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// canonical 槽位在左右镜像印版上的真实源矩形；调用方先把物理槽位映射到 canonical 列。
    /// 以有效区域右边界反射整条矩形边界，保留奇数宽度产生的不等宽列。
    /// </summary>
    public static PixelRegion? ReflectedSlotRect(GridLayout layout, GridSlot slot)
    {
        var rect = SlotRect(layout, slot);
        if (rect is not { } source)
        {
            return null;
        }

        var region = layout.Region;
        return new PixelRegion(
            region.Right - (source.Right - region.X),
            source.Y,
            source.Width,
            source.Height);
    }

    /// <summary>
    /// 这一格必须落在整图内，否则这张卡切不完整，不能当成可分发。
    /// 有效区域设错（例如把整幅宽度当成单卡宽度的一半）时由它挡住。
    /// </summary>
    public static bool Fits(PixelRegion region, int imageWidth, int imageHeight) =>
        region.X >= 0
        && region.Y >= 0
        && region.Width > 0
        && region.Height > 0
        && region.Right <= imageWidth
        && region.Bottom <= imageHeight;

    private static int Scale(int index, int length, int count) => (int)((long)index * length / count);
}
