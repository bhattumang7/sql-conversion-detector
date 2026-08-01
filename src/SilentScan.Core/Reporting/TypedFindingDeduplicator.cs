using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

/// <summary>
/// Collapses <see cref="TypedPredicateFinding"/>s that describe the SAME underlying defect -
/// same table, column, operator, and other-operand shape - down to one representative each.
/// Real-world corpora routinely re-issue the identical CREATE/ALTER across many incremental
/// upgrade scripts (DNN Platform's 291 .SqlDataProvider files are the case that surfaced this:
/// one `CreatedByUser = @intCreatedByUser`-shaped bug on `dbo.Documents`, textually repeated
/// across 6 version-history files, is one defect, not six). A raw finding count conflates
/// "distinct bugs" with "how many times this file layout happened to repeat a CREATE" - a
/// prevalence study needs the former, not the latter (CLAUDE.md precision discipline: an
/// inflated count is its own kind of false claim, even though every individual finding is
/// real).
/// </summary>
public static class TypedFindingDeduplicator
{
    /// <summary>
    /// Returns one representative finding per distinct (table, column, operator, other-operand
    /// shape) key - the earliest in <paramref name="findings"/>' existing deterministic order,
    /// so the result is itself deterministic. An other operand that couldn't be typed at all
    /// still merges with an identically-shaped untyped finding on the same table/column/
    /// operator - the same repeated-CREATE scenario this exists to collapse produces identical
    /// Unknown-typed operands too, not just typed ones.
    /// </summary>
    public static IReadOnlyList<TypedPredicateFinding> Dedupe(IReadOnlyList<TypedPredicateFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = findings.Where(finding => seen.Add(Key(finding))).ToList();

        return result;
    }

    /// <summary>
    /// The dedup identity: WHERE the column lives (table + column name), HOW it's compared
    /// (operator), and WHAT it's compared against, described at the level of detail that
    /// actually decides the verdict - a type category and, for string-family operands, the
    /// collation - rather than incidental facts like the literal's exact text or which of two
    /// textually-identical CREATE PROCEDURE copies happened to be scanned.
    /// </summary>
    private static string Key(TypedPredicateFinding finding)
    {
        var otherShape = finding.OtherOperand switch
        {
            PredicateOperand.Value { Type: { } type } => $"Value:{type.Category}:{(type.IsStringFamily ? type.Collation?.Name ?? "?" : string.Empty)}",
            PredicateOperand.Value => "Value:Unresolved",
            PredicateOperand.Column otherColumn => $"Column:{otherColumn.TableQualifiedName}.{otherColumn.ColumnName}",
            _ => "Unknown",
        };

        return string.Join(
            '',
            finding.Column.TableQualifiedName,
            finding.Column.ColumnName,
            finding.Operator,
            otherShape);
    }
}
