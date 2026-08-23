using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Core.Common;

namespace SilentScan.Verify.Oracle;

public sealed class ScalarUdfVerifier
{
    private const string InlinedMarker = "ContainsInlineScalarTsqlUdfs=\"1\"";
    private static readonly XNamespace ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly PlanXmlCapture _planXmlCapture;
    private readonly FunctionParameterReader _functionParameterReader;

    public ScalarUdfVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _functionParameterReader = new FunctionParameterReader(options);
    }

    public async Task<ScalarUdfResult> VerifyAsync(string database, ScalarUdfFinding finding, CancellationToken cancellationToken = default)
    {
        if (finding.Kind == ScalarUdfFindingKind.SchemaDependency)
        {
            return new ScalarUdfResult(
                finding, ScalarUdfOutcome.NotProbeable,
                "SchemaDependency findings are catalog-definitive - the constraint/computed-column definition text IS engine truth, so a plan probe adds no evidence beyond what the catalog already asserts.");
        }

        var parameterTypes = await _functionParameterReader.TryGetParameterTypesAsync(database, finding.FunctionQualifiedName, cancellationToken);
        var pinnedProbe = ScalarUdfProbeBuilder.BuildInvocationProbe(finding, parameterTypes, pinInlining: true);
        if (pinnedProbe is null)
        {
            return new ScalarUdfResult(
                finding, ScalarUdfOutcome.NotProbeable,
                $"Could not resolve/render '{finding.FunctionQualifiedName}'s own parameter types into a dummy argument list.");
        }

        string pinnedPlanXml;
        try
        {
            pinnedPlanXml = await _planXmlCapture.CaptureAsync(database, pinnedProbe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new ScalarUdfResult(finding, ScalarUdfOutcome.ProbeFailed, ex.Message);
        }

        if (!HasMatchingUserDefinedFunction(pinnedPlanXml, finding.FunctionQualifiedName))
        {
            return new ScalarUdfResult(
                finding, ScalarUdfOutcome.NotConfirmed,
                $"The pinned probe for '{finding.FunctionQualifiedName}' (scalar-UDF inlining disabled) shows no UserDefinedFunction plan element - contradicts the finding's own claim that this is a scalar UDF reference.");
        }

        if (finding.Inlineability == ScalarUdfInlineability.Unknown)
        {
            return new ScalarUdfResult(finding, ScalarUdfOutcome.Confirmed, null);
        }

        var naturalProbe = ScalarUdfProbeBuilder.BuildInvocationProbe(finding, parameterTypes, pinInlining: false);
        if (naturalProbe is null)
        {
            return new ScalarUdfResult(finding, ScalarUdfOutcome.Confirmed, null);
        }

        string naturalPlanXml;
        try
        {
            naturalPlanXml = await _planXmlCapture.CaptureAsync(database, naturalProbe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new ScalarUdfResult(finding, ScalarUdfOutcome.ProbeFailed, ex.Message);
        }

        var engineInlinedNaturally = naturalPlanXml.Contains(InlinedMarker, StringComparison.Ordinal);
        var engineDidNotInlineNaturally = HasMatchingUserDefinedFunction(naturalPlanXml, finding.FunctionQualifiedName);

        if (finding.Inlineability == ScalarUdfInlineability.NotInlineable && engineInlinedNaturally)
        {
            return new ScalarUdfResult(
                finding, ScalarUdfOutcome.NotConfirmed,
                $"The finding claims '{finding.FunctionQualifiedName}' is not inlineable, but the natural probe shows the engine inlining it (ContainsInlineScalarTsqlUdfs) - contradicts the blocker scan/engine flag.");
        }

        if (finding.Inlineability == ScalarUdfInlineability.Inlineable && engineDidNotInlineNaturally)
        {
            return new ScalarUdfResult(
                finding, ScalarUdfOutcome.NotConfirmed,
                $"The finding claims '{finding.FunctionQualifiedName}' is inlineable, but the natural probe still shows a UserDefinedFunction plan element - contradicts the engine's own is_inlineable flag.");
        }

        return new ScalarUdfResult(finding, ScalarUdfOutcome.Confirmed, null);
    }

    private static bool HasMatchingUserDefinedFunction(string planXml, string qualifiedName)
    {
        var doc = XDocument.Parse(planXml);
        return doc.Descendants(ShowPlanNs + "UserDefinedFunction")
            .Any(udf => NamesSameFunction((string?)udf.Attribute("FunctionName"), qualifiedName));
    }

    private static bool NamesSameFunction(string? planFunctionName, string qualifiedName)
    {
        if (planFunctionName is null)
        {
            return false;
        }

        var stripped = planFunctionName.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        return string.Equals(stripped, qualifiedName, StringComparison.OrdinalIgnoreCase)
            || stripped.EndsWith("." + qualifiedName, StringComparison.OrdinalIgnoreCase);
    }
}
