using System.Xml.Linq;

namespace SilentScan.Verify.Oracle;

public static class PlanAffectingConvertDetector
{
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static IReadOnlyList<PlanAffectingConvertFinding> FindWarnings(string planXml)
    {
        var doc = XDocument.Parse(planXml);

        return doc.Descendants(ShowPlanNs + "PlanAffectingConvert")
            .Select(warning => new PlanAffectingConvertFinding(
                ConvertIssue: (string?)warning.Attribute("ConvertIssue") ?? string.Empty,
                Expression: (string?)warning.Attribute("Expression") ?? string.Empty))
            .ToList();
    }
}
