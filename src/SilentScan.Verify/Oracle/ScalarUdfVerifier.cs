using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Core.Common;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Oracle-confirms a <see cref="ScalarUdfFinding"/> (docs/detection-checklist.md Tier 1 #1) with
/// a two-probe design, both oracle-verified directly against the local Docker instance:
/// <list type="number">
/// <item>
/// A PINNED probe (<c>OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))</c>) confirms the
/// core claim - "this is a real scalar UDF reference" - independent of whether SQL 2019+ FROID
/// inlining would otherwise fold it away. Marker: a <c>&lt;UserDefinedFunction FunctionName="..."&gt;</c>
/// plan element. Verified: even a trivially inlineable function (a single <c>RETURN expr</c>
/// body) produces this element under the hint.
/// </item>
/// <item>
/// A NATURAL probe (no hint) cross-checks the finding's own <see cref="ScalarUdfInlineability"/>
/// read against what the engine actually does when left to decide: inlined away leaves
/// <c>ContainsInlineScalarTsqlUdfs="1"</c> on the enclosing <c>StmtSimple</c> and no
/// <c>UserDefinedFunction</c> element; not inlined leaves the element and no that attribute. A
/// disagreement disciplines the blocker-scan/engine-flag plumbing itself, not just the reach
/// claim - e.g. a finding claiming NotInlineable whose natural probe the engine visibly inlines
/// anyway.
/// </item>
/// </list>
/// Neither probe reuses <see cref="ScalarUdfFinding.ReferencedObjectQualifiedName"/> for a
/// <see cref="ScalarUdfFindingKind.NestedUnderViewOrTvf"/> finding - see
/// <see cref="ScalarUdfProbeBuilder"/>'s own doc comment for why probing through the view instead
/// would silently give the wrong answer (the hint does not propagate into a view's own algebrized
/// definition, oracle-verified). <see cref="ScalarUdfFindingKind.SchemaDependency"/> is never
/// probed at all: the constraint/computed-column definition text this finding cites IS engine
/// truth (<c>sys.default_constraints</c>/<c>sys.check_constraints</c>/<c>sys.computed_columns</c>
/// <c>.definition</c>), so a plan probe would add no evidence beyond what the catalog already
/// asserts.
/// </summary>
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

        // Nothing left to cross-check for Unknown - it isn't a falsifiable claim about what the
        // engine does, only "this scan's own blocker list found nothing," so the natural probe
        // adds no signal either way.
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

    // Checking for a bare "<UserDefinedFunction" anywhere in the plan document would confirm off
    // an entirely unrelated scalar UDF the same batch happens to reference - this parses the plan
    // and requires the element's own FunctionName to name THIS finding's function.
    private static bool HasMatchingUserDefinedFunction(string planXml, string qualifiedName)
    {
        var doc = XDocument.Parse(planXml);
        return doc.Descendants(ShowPlanNs + "UserDefinedFunction")
            .Any(udf => NamesSameFunction((string?)udf.Attribute("FunctionName"), qualifiedName));
    }

    // The plan's FunctionName can be database-qualified (three parts) while
    // finding.FunctionQualifiedName never is (SchemaObjectNameHelper.QualifyFunctionCall always
    // renders schema.name) - an exact match would false-negative on that, so this checks that the
    // plan name's own trailing "schema.name" matches, database prefix or not.
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
