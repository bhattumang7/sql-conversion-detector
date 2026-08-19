using SilentScan.Live;

namespace SilentScan.Tests.Integration;

/// <summary>
/// The gap this fix closes: LiveScanRunner's own workload-finding dedup key used the default
/// (ordinal, case-SENSITIVE) tuple comparer, unlike every other identifier map in this codebase
/// (DatabaseCatalog's own dictionaries are all OrdinalIgnoreCase) - a casing difference between
/// the module-body pass's own table/column name and the live plan cache's own would miss the
/// match and publish a duplicate workload finding for an already-reported column.
/// </summary>
public sealed class TupleOrdinalIgnoreCaseComparerTests
{
    [Fact]
    public void Equals_DifferingCaseOnEitherElement_TreatedAsEqual()
    {
        var comparer = TupleOrdinalIgnoreCaseComparer.Instance;

        Assert.True(comparer.Equals(("dbo.Orders", "OrderCode"), ("dbo.orders", "OrderCode")));
        Assert.True(comparer.Equals(("dbo.Orders", "OrderCode"), ("dbo.Orders", "ordercode")));
        Assert.True(comparer.Equals(("DBO.ORDERS", "ORDERCODE"), ("dbo.orders", "ordercode")));
    }

    [Fact]
    public void Equals_GenuinelyDifferentNames_NotEqual()
    {
        var comparer = TupleOrdinalIgnoreCaseComparer.Instance;

        Assert.False(comparer.Equals(("dbo.Orders", "OrderCode"), ("dbo.OrderLines", "OrderCode")));
    }

    [Fact]
    public void GetHashCode_DifferingCaseOnEitherElement_ProducesTheSameHash()
    {
        var comparer = TupleOrdinalIgnoreCaseComparer.Instance;

        Assert.Equal(
            comparer.GetHashCode(("dbo.Orders", "OrderCode")),
            comparer.GetHashCode(("dbo.orders", "ORDERCODE")));
    }

    [Fact]
    public void HashSet_WithComparer_DeduplicatesCaseInsensitively()
    {
        var set = new HashSet<(string, string)>(TupleOrdinalIgnoreCaseComparer.Instance) { ("dbo.Orders", "OrderCode") };

        Assert.Contains(("dbo.orders", "ordercode"), set);
    }
}
