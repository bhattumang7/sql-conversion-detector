using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Checks a showplan XML for whether a specific index was used with a Seek - the oracle
/// confirmation signal for expression-derived findings (CLAUDE.md Verify workflow, extended):
/// unlike the CONVERT_IMPLICIT signal (which only applies to *implicit* conversions), a CAST
/// buried in an upstream view is an *explicit* conversion, so it never appears as
/// CONVERT_IMPLICIT in the plan. What confirms the finding instead is the absence of an Index
/// Seek on the column's own index, proving the engine really can't use it - not just that our
/// classifier predicts it can't.
/// </summary>
public static class IndexAccessDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>True if the plan contains an Index Seek (not just a scan) against <paramref name="indexName"/>.</summary>
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
