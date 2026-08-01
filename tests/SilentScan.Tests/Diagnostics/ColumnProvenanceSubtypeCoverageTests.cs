using System.Reflection;
using SilentScan.Core.Lineage;

namespace SilentScan.Tests.Diagnostics;

/// <summary>
/// The forcing function the "deny-by-default accounting" pass wanted: several switch
/// expressions over <see cref="ColumnProvenance"/> (<c>ScalarExpressionResolver.
/// BumpDepthIfViewLayer</c>, <c>ColumnProvenanceAnalysis.TryGetScalarType</c>) need a case for
/// every concrete subtype, but the C# compiler does not treat this sealed-nested-record set as
/// a closed union - it still demands a `_` discard even when every known case is listed, so a
/// genuinely new subtype can silently fall through that discard with no compile error at all.
/// This test reflects over the real subtype set and pins it to an explicit, named list: adding
/// a 7th ColumnProvenance subtype fails this test immediately, forcing a deliberate look at
/// every `_ => ...` site that switches on it, rather than a silent fallthrough discovered later
/// as a wrong-verdict finding.
/// </summary>
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

        // If this assertion fails, a new ColumnProvenance subtype was added. Before updating
        // this list, audit every `_ => ...`/discard arm in a switch over ColumnProvenance -
        // ScalarExpressionResolver.BumpDepthIfViewLayer and
        // ColumnProvenanceAnalysis.TryGetScalarType are the two known sites - and decide
        // deliberately what the new case should do there.
        var expected = KnownSubtypeNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }
}
