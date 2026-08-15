using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Oracle-confirms a <see cref="TvfFenceFinding"/> (docs/detection-checklist.md Tier 1 #2).
/// The marker is plan SHAPE, oracle-verified directly against the local Docker instance: a
/// multi-statement/CLR TVF reference produces a <c>PhysicalOp="Table-valued function"</c> RelOp
/// (with the fixed 1/100-row cardinality guess as its own <c>EstimateRows</c>); an inline TVF
/// reference dissolves into ordinary base operators and never produces that node - confirmed by
/// probing both shapes against a scratch database and diffing the captured plan XML.
/// <c>INSERT ... EXEC</c> has its own marker, <c>StatementType="INSERT EXEC"</c>, also
/// oracle-verified directly.
/// </summary>
public sealed class TvfFenceVerifier
{
    private const string TableValuedFunctionMarker = "PhysicalOp=\"Table-valued function\"";
    private const string InsertExecMarker = "StatementType=\"INSERT EXEC\"";

    private readonly PlanXmlCapture _planXmlCapture;
    private readonly FunctionParameterReader _functionParameterReader;
    private readonly ProcedureParameterReader _procedureParameterReader;
    private readonly ProcedureResultColumnReader _procedureResultColumnReader;

    public TvfFenceVerifier(SqlServerOptions options)
    {
        _planXmlCapture = new PlanXmlCapture(options);
        _functionParameterReader = new FunctionParameterReader(options);
        _procedureParameterReader = new ProcedureParameterReader(options);
        _procedureResultColumnReader = new ProcedureResultColumnReader(options);
    }

    public async Task<TvfFenceResult> VerifyAsync(
        string database, TvfFenceFinding finding, CancellationToken cancellationToken = default) =>
        finding.Kind == TvfFenceFindingKind.InsertExec
            ? await VerifyInsertExecAsync(database, finding, cancellationToken)
            : await VerifyFunctionReferenceAsync(database, finding, cancellationToken);

    private async Task<TvfFenceResult> VerifyFunctionReferenceAsync(string database, TvfFenceFinding finding, CancellationToken cancellationToken)
    {
        if (finding.FunctionQualifiedName is not { } qualifiedName)
        {
            return new TvfFenceResult(finding, TvfFenceOutcome.NotProbeable, "No function qualified name on the finding.");
        }

        var parameterTypes = await _functionParameterReader.TryGetParameterTypesAsync(database, qualifiedName, cancellationToken);
        var probe = TvfFenceProbeBuilder.BuildFunctionProbe(finding, parameterTypes);
        if (probe is null)
        {
            return new TvfFenceResult(
                finding, TvfFenceOutcome.NotProbeable,
                $"Could not resolve/render '{qualifiedName}'s own parameter types into a dummy argument list.");
        }

        string planXml;
        try
        {
            planXml = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new TvfFenceResult(finding, TvfFenceOutcome.ProbeFailed, ex.Message);
        }

        var hasFenceOperator = planXml.Contains(TableValuedFunctionMarker, StringComparison.Ordinal);
        return hasFenceOperator
            ? new TvfFenceResult(finding, TvfFenceOutcome.Confirmed, null)
            : new TvfFenceResult(
                finding, TvfFenceOutcome.NotConfirmed,
                $"The plan for '{qualifiedName}' shows no Table-valued function operator - it dissolved into base operators like an inline TVF, contradicting the finding's own claim.");
    }

    private async Task<TvfFenceResult> VerifyInsertExecAsync(string database, TvfFenceFinding finding, CancellationToken cancellationToken)
    {
        if (finding.ReferencedObjectQualifiedName is not { } procedureQualifiedName)
        {
            return new TvfFenceResult(finding, TvfFenceOutcome.NotProbeable, "No procedure qualified name on the finding.");
        }

        var parameterTypes = await _procedureParameterReader.TryGetParameterTypesAsync(database, procedureQualifiedName, cancellationToken);
        if (parameterTypes is null)
        {
            return new TvfFenceResult(
                finding, TvfFenceOutcome.NotProbeable,
                $"Could not resolve/render '{procedureQualifiedName}'s own parameter types into a dummy argument list (an OUTPUT or table-valued parameter, or an unrenderable type).");
        }

        var describeProbe = TvfFenceProbeBuilder.BuildExecDescribeProbe(procedureQualifiedName, parameterTypes);
        if (describeProbe is null)
        {
            return new TvfFenceResult(finding, TvfFenceOutcome.NotProbeable, $"Could not render a dummy EXEC argument list for '{procedureQualifiedName}'.");
        }

        var resultColumns = await _procedureResultColumnReader.TryDescribeResultColumnsAsync(database, describeProbe, cancellationToken);
        var probe = TvfFenceProbeBuilder.BuildInsertExecProbe(finding, resultColumns, parameterTypes);
        if (probe is null)
        {
            return new TvfFenceResult(
                finding, TvfFenceOutcome.NotProbeable,
                $"The engine could not describe '{procedureQualifiedName}'s own first result set (no result set at all, or a shape that varies by branch), so no receiving table variable could be synthesized.");
        }

        string planXml;
        try
        {
            planXml = await _planXmlCapture.CaptureAsync(database, probe, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new TvfFenceResult(finding, TvfFenceOutcome.ProbeFailed, ex.Message);
        }

        var hasInsertExecStatement = planXml.Contains(InsertExecMarker, StringComparison.Ordinal);
        return hasInsertExecStatement
            ? new TvfFenceResult(finding, TvfFenceOutcome.Confirmed, null)
            : new TvfFenceResult(finding, TvfFenceOutcome.NotConfirmed, "The plan's statement type was not INSERT EXEC - contradicts the finding's own claim.");
    }
}
