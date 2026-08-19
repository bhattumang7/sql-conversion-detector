using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Extracts every real table-column binding the engine's own algebrizer produced in a plan XML -
/// the binder-parity regression guard docs/detection-checklist.md's Phase 1.5 "one binder" item
/// calls for, extending the lineage-parity pattern (CLAUDE.md: <c>dm_exec_describe_first_result_set</c>
/// vs cached <c>sys.columns</c>) from result-set shape to predicate binding. Unlike <see
/// cref="ConvertImplicitDetector"/>, which is deliberately scoped to CONVERT_IMPLICIT nodes only,
/// this walks every <c>&lt;ColumnReference&gt;</c> in the plan unconditionally - the assertion
/// this feeds is "does SilentScan's own statically-resolved <c>ColumnProvenance.BaseColumn</c>
/// agree with what the real engine actually bound this reference to," not just the conversion
/// subset.
/// </summary>
public static class BinderParityDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static IReadOnlyList<ResolvedColumnReference> FindAllColumnReferences(string planXml)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "ColumnReference")
            // A local variable/parameter also serializes as <ColumnReference> (Column="@p") but
            // carries no Table attribute - not a real table-column binding, so excluded here the
            // same way ConvertImplicitDetector excludes it.
            .Where(c => !string.IsNullOrEmpty((string?)c.Attribute("Table")))
            .Select(c => new ResolvedColumnReference(
                Database: TrimBrackets((string?)c.Attribute("Database")),
                Schema: TrimBrackets((string?)c.Attribute("Schema")),
                Table: TrimBrackets((string?)c.Attribute("Table")),
                Column: (string?)c.Attribute("Column")))
            .ToList();
    }

    private static string? TrimBrackets(string? bracketedIdentifier) =>
        bracketedIdentifier?.Trim('[', ']');
}
