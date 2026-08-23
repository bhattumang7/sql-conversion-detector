using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

public static class BinderParityDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static IReadOnlyList<ResolvedColumnReference> FindAllColumnReferences(string planXml)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "ColumnReference")
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
