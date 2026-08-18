using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Finds CONVERT_IMPLICIT applied to a COLUMN in a showplan XML - the oracle confirmation
/// signal for SCAN_FORCED findings (CLAUDE.md: "search ScalarOperator/Convert with
/// Implicit=\"true\" over a ColumnReference"). A Convert whose input is a ColumnReference
/// means the COLUMN converted, which is what loses the seek; a Convert over a parameter or
/// literal means the harmless side converted and is not reported here.
/// </summary>
public static class ConvertImplicitDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private const string TableAttributeName = "Table";

    public static IReadOnlyList<ConvertImplicitFinding> FindColumnConversions(string planXml)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "Convert")
            .Where(convert => IsImplicit((string?)convert.Attribute("Implicit")))
            .Select(convert => new
            {
                Convert = convert,
                // Showplan XML represents BOTH real table columns and local
                // variables/parameters as <ColumnReference> - the only distinguishing
                // signal is that a genuine table column has a non-empty Table attribute,
                // while a parameter (e.g. Column="@p") does not. Found during the Phase 4
                // corpus pilot: without this check, a Convert applied to a @parameter
                // (the harmless, correct-direction case) was misreported as a column-side
                // conversion.
                ColumnRef = convert.Descendants(ShowPlanNs + "ColumnReference")
                    .FirstOrDefault(c => !string.IsNullOrEmpty((string?)c.Attribute(TableAttributeName))),
            })
            .Where(x => x.ColumnRef is not null)
            .Select(x => new ConvertImplicitFinding(
                Database: TrimBrackets((string?)x.ColumnRef!.Attribute("Database")),
                Schema: TrimBrackets((string?)x.ColumnRef.Attribute("Schema")),
                Table: TrimBrackets((string?)x.ColumnRef.Attribute(TableAttributeName)),
                Column: (string?)x.ColumnRef.Attribute("Column"),
                ConvertedToDataType: (string?)x.Convert.Attribute("DataType") ?? "unknown",
                RangeSeekBound: IsRangeSeekBound(x.Convert, x.ColumnRef)))
            .ToList();
    }

    private static string? TrimBrackets(string? bracketedIdentifier) =>
        bracketedIdentifier?.Trim('[', ']');

    // GetRangeThroughConvert only ever bounds the specific column it was invoked for - the
    // engine surfaces that binding as a RangeColumns/ColumnReference entry inside the
    // SeekPredicates of the very same RelOp (IndexSeek/IndexScan) whose own residual Predicate
    // carries this Convert node (oracle-verified: a two-branch UNION ALL plan with one
    // Windows-collation range-seek column and one SQL_*-collation scan-forced column in the same
    // cached plan shows GetRangeThroughConvert and this column's own RangeColumns entry both
    // scoped to the range-seeking branch's RelOp only - never the sibling scan branch's). Walking
    // up to the nearest ancestor RelOp and checking THAT operator's own SeekPredicates - rather
    // than a plan.Contains("GetRangeThroughConvert") over the whole document - is what makes this
    // per-conversion instead of per-plan; both formats the engine emits across compat levels
    // (SeekPredicateNew and the legacy SeekPredicate) are checked, since a range-bound column can
    // land in either depending on the plan's compatibility level.
    private static bool IsRangeSeekBound(XElement convert, XElement columnRef)
    {
        var owningRelOp = convert.Ancestors(ShowPlanNs + "RelOp").FirstOrDefault();
        if (owningRelOp is null)
        {
            return false;
        }

        var database = (string?)columnRef.Attribute("Database");
        var schema = (string?)columnRef.Attribute("Schema");
        var table = (string?)columnRef.Attribute(TableAttributeName);
        var column = (string?)columnRef.Attribute("Column");

        return owningRelOp.Descendants(ShowPlanNs + "SeekPredicates")
            .Concat(owningRelOp.Descendants(ShowPlanNs + "SeekPredicate"))
            .Descendants(ShowPlanNs + "RangeColumns")
            .Descendants(ShowPlanNs + "ColumnReference")
            .Any(rangeColumnRef =>
                string.Equals((string?)rangeColumnRef.Attribute("Database"), database, StringComparison.Ordinal)
                && string.Equals((string?)rangeColumnRef.Attribute("Schema"), schema, StringComparison.Ordinal)
                && string.Equals((string?)rangeColumnRef.Attribute(TableAttributeName), table, StringComparison.Ordinal)
                && string.Equals((string?)rangeColumnRef.Attribute("Column"), column, StringComparison.Ordinal));
    }

    // Showplan XML's Implicit attribute is typed xsd:boolean, which permits both the "1"/"0"
    // and "true"/"false" lexical forms - verified against the schema (and this class's own
    // original doc comment, which already said "true" while the code only ever checked "1").
    // Different SQL Server versions/serialization paths are free to emit either.
    private static bool IsImplicit(string? value) => value is "1" or "true";
}
