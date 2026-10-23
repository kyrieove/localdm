namespace LocalDm.Core.Tablet;

/// <summary>全屏看完某页后，阅读器应该从哪一页开始显示这一屏；保持原来的分屏对齐</summary>
public static class SourceSpread
{
    public static int StartFor(int currentStart, int page, int columns, int pageCount)
    {
        var cols = Math.Max(1, columns);
        var floorDiv = (int)Math.Floor((double)(page - currentStart) / cols);
        var start = currentStart + floorDiv * cols;
        return Math.Clamp(start, 1, Math.Max(1, pageCount));
    }
}
