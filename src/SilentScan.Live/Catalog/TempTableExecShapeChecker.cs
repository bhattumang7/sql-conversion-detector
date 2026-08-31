using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

public sealed class TempTableExecShapeChecker
{
    private readonly string _connectionString;

    public TempTableExecShapeChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<TempTableExecShapeReport> CheckAsync(
        IReadOnlyList<TempTableExecShapeCandidate> candidates, IScanStage? stage = null, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return TempTableExecShapeReport.Empty;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var parametersByProc = await LiveDescribedColumnReader.ReadProcedureParametersAsync(connection, cancellationToken);

        var findings = new List<TempTableExecShapeFinding>();
        var unanalyzed = new List<UnanalyzedTempTableExecSite>();

        foreach (var candidate in candidates)
        {
            stage?.Advance(currentItem: candidate.TempTableQualifiedName);

            if (candidate.TempTableColumns is not { } tempColumns)
            {
                unanalyzed.Add(new UnanalyzedTempTableExecSite(
                    candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                    "the temp table's own declared shape could not be resolved in the catalog",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            var parameters = parametersByProc.TryGetValue(candidate.ExecutedProcQualifiedName, out var found)
                ? found
                : [];

            var (probe, unrenderableReason) = LiveDescribeProbeBuilder.BuildProcedureProbe(candidate.ExecutedProcQualifiedName, parameters);
            if (probe is null)
            {
                unanalyzed.Add(new UnanalyzedTempTableExecSite(
                    candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                    $"executed proc's own parameter list could not be probed: {unrenderableReason}",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            var described = await LiveDescribedColumnReader.DescribeProcedureOrderedAsync(connection, probe, cancellationToken);
            if (described.IsError)
            {
                unanalyzed.Add(new UnanalyzedTempTableExecSite(
                    candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                    $"executed proc could not be described (Msg {described.ErrorNumber}: {described.ErrorMessage})",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            Classify(candidate, tempColumns, described.Columns!, findings);
        }

        return new TempTableExecShapeReport(
            [.. findings.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.ColumnPosition)],
            [.. unanalyzed.OrderBy(u => u.SourcePath, StringComparer.Ordinal).ThenBy(u => u.Line)]);
    }

    internal static void Classify(
        TempTableExecShapeCandidate candidate,
        IReadOnlyList<CatalogColumn> tempColumns,
        IReadOnlyList<DescribedResultColumn> describedColumns,
        List<TempTableExecShapeFinding> findings)
    {
        if (tempColumns.Count != describedColumns.Count)
        {
            findings.Add(new TempTableExecShapeFinding(
                TempTableExecShapeFindingKind.ColumnCountMismatch,
                candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                tempColumns.Count, describedColumns.Count,
                ColumnName: null, ColumnPosition: null, TempColumnTypeDisplay: null, DescribedColumnTypeDisplay: null, WriteLoss: null,
                candidate.CallerScopeQualifiedName, candidate.SourcePath, candidate.Line, candidate.Column));
            return;
        }

        for (var i = 0; i < tempColumns.Count; i++)
        {
            var tempColumn = tempColumns[i];
            var describedType = LiveTypeMapper.BuildType(
                describedColumns[i].Column.TypeName, describedColumns[i].Column.MaxLength,
                describedColumns[i].Column.Precision, describedColumns[i].Column.Scale, describedColumns[i].Column.CollationName);

            var kind = WriteLossClassifier.Classify(tempColumn.Type, describedType, sourceExpression: null, isVariableTarget: false);
            if (kind is null)
            {
                continue;
            }

            findings.Add(new TempTableExecShapeFinding(
                TempTableExecShapeFindingKind.ColumnTypeMismatch,
                candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                tempColumns.Count, describedColumns.Count,
                ColumnName: tempColumn.Name, ColumnPosition: i + 1,
                TempColumnTypeDisplay: tempColumn.Type?.ToString(), DescribedColumnTypeDisplay: describedType?.ToString(), WriteLoss: kind,
                candidate.CallerScopeQualifiedName, candidate.SourcePath, candidate.Line, candidate.Column));
        }
    }
}
