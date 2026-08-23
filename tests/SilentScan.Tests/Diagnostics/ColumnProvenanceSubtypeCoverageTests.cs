using System.Reflection;
using SilentScan.Core.Lineage;

namespace SilentScan.Tests.Diagnostics;

public sealed class ColumnProvenanceSubtypeCoverageTests
{
    private static readonly string[] KnownSubtypeNames = ["BaseColumn", "Cast", "Declared", "Expression", "Union", "Unknown"];

    [Fact]
    public void ColumnProvenance_KnownSubtypes_MatchesThePinnedList()
    {
        var actual = typeof(ColumnProvenance).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => typeof(ColumnProvenance).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var expected = KnownSubtypeNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }
}
