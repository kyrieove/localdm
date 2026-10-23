using LocalDm.Core.Tablet;

namespace LocalDm.Tests;

public sealed class SourceSpreadTests
{
    [Theory]
    [InlineData(1, 4, 2, 12, 3)]
    [InlineData(3, 1, 2, 12, 1)]
    [InlineData(2, 1, 2, 12, 1)]
    [InlineData(2, 5, 2, 12, 4)]
    [InlineData(5, 5, 1, 12, 5)]
    [InlineData(1, 12, 2, 12, 11)]
    [InlineData(1, 4, 2, 0, 1)]
    public void StartFor_computes_spread_start_with_clamping(int currentStart, int page, int columns, int pageCount, int expected)
    {
        var actual = SourceSpread.StartFor(currentStart, page, columns, pageCount);
        Assert.Equal(expected, actual);
    }
}
