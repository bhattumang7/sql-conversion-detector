using Microsoft.Data.SqlClient;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

public sealed class ExecResultSetsShapeChecker
{
    private readonly string _connectionString;

    public ExecResultSetsShapeChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<ExecResultSetsShapeReport> CheckAsync(
        IReadOnlyList<ExecResultSetsShapeCandidate> candidates, IScanStage? stage = null, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return ExecResultSetsShapeReport.Empty;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var parametersByProc = await LiveDescribedColumnReader.ReadProcedureParametersAsync(connection, cancellationToken);

        var findings = new List<ExecResultSetsShapeFinding>();
        var unanalyzed = new List<UnanalyzedExecResultSetsSite>();

        foreach (var candidate in candidates)
        {
            stage?.Advance(currentItem: candidate.ExecutedProcQualifiedName);

            var parameters = parametersByProc.TryGetValue(candidate.ExecutedProcQualifiedName, out var found)
                ? found
                : [];

            var (probe, unrenderableReason) = LiveDescribeProbeBuilder.BuildProcedureProbe(candidate.ExecutedProcQualifiedName, parameters);
            if (probe is null)
            {
                unanalyzed.Add(new UnanalyzedExecResultSetsSite(
                    candidate.ExecutedProcQualifiedName,
                    $"executed proc's own parameter list could not be probed: {unrenderableReason}",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            var described = await LiveDescribedColumnReader.DescribeProcedureOrderedAsync(connection, probe, cancellationToken);
            if (described.IsError)
            {
                unanalyzed.Add(new UnanalyzedExecResultSetsSite(
                    candidate.ExecutedProcQualifiedName,
                    $"executed proc could not be described (Msg {described.ErrorNumber}: {described.ErrorMessage})",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            Classify(candidate, described.Columns!, findings);
        }

        return new ExecResultSetsShapeReport(
            [.. findings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.ColumnPosition)],
            [.. unanalyzed.OrderBy(u => u.SourcePath, StringComparer.Ordinal).ThenBy(u => u.Line)]);
    }

    internal static void Classify(
        ExecResultSetsShapeCandidate candidate,
        IReadOnlyList<DescribedResultColumn> describedColumns,
        List<ExecResultSetsShapeFinding> findings)
    {
        if (candidate.DeclaredColumns.Count != describedColumns.Count)
        {
            findings.Add(new ExecResultSetsShapeFinding(
                ExecResultSetsShapeFindingKind.ColumnCountMismatch,
                candidate.ExecutedProcQualifiedName,
                candidate.DeclaredColumns.Count, describedColumns.Count,
                ColumnName: null, ColumnPosition: null, DeclaredColumnTypeDisplay: null, DescribedColumnTypeDisplay: null, WriteLoss: null,
                candidate.CallerScopeQualifiedName, candidate.SourcePath, candidate.Line, candidate.Column));
            return;
        }

        for (var i = 0; i < candidate.DeclaredColumns.Count; i++)
        {
            var declaredColumn = candidate.DeclaredColumns[i];
            var describedType = LiveTypeMapper.BuildType(
                describedColumns[i].Column.TypeName, describedColumns[i].Column.MaxLength,
                describedColumns[i].Column.Precision, describedColumns[i].Column.Scale, describedColumns[i].Column.CollationName);

            var kind = WriteLossClassifier.Classify(declaredColumn.Type, describedType, sourceExpression: null, isVariableTarget: true);
            if (kind is null)
            {
                continue;
            }

            findings.Add(new ExecResultSetsShapeFinding(
                ExecResultSetsShapeFindingKind.ColumnTypeMismatch,
                candidate.ExecutedProcQualifiedName,
                candidate.DeclaredColumns.Count, describedColumns.Count,
                ColumnName: declaredColumn.Name, ColumnPosition: i + 1,
                DeclaredColumnTypeDisplay: declaredColumn.Type.ToString(), DescribedColumnTypeDisplay: describedType?.ToString(), WriteLoss: kind,
                candidate.CallerScopeQualifiedName, candidate.SourcePath, candidate.Line, candidate.Column));
        }
    }
}
