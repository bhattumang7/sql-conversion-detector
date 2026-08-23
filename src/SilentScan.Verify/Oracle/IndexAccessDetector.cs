using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

public static class IndexAccessDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

public static bool HasIndexSeek(string planXml, string indexName)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "RelOp")
            .Where(relOp => (string?)relOp.Attribute("PhysicalOp") is { } physicalOp && physicalOp.Contains("Seek", StringComparison.Ordinal))
            .SelectMany(relOp => relOp.Descendants(ShowPlanNs + "Object"))
            .Any(obj => string.Equals(TrimBrackets((string?)obj.Attribute("Index")), indexName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TrimBrackets(string? bracketedIdentifier) =>
        bracketedIdentifier?.Trim('[', ']');
}
