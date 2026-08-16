using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// The live-round-trip half of docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3 -
/// <see cref="TempTableExecShapeCandidateScanner"/> finds every <c>INSERT INTO #temp EXEC proc</c>
/// site and resolves the caller-side temp table's own declared columns (no network needed for
/// that); this class describes each site's EXECUTED proc via
/// <c>sys.dm_exec_describe_first_result_set</c> (compile-only,
/// <see cref="LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly"/>) and compares the two
/// shapes POSITIONALLY - the only binding T-SQL actually uses for <c>INSERT ... EXEC</c>. Live-
/// mode only, mirroring <see cref="LiveLineageParityChecker"/>'s own constructor shape.
/// </summary>
public sealed class TempTableExecShapeChecker
{
    private readonly string _connectionString;

    public TempTableExecShapeChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<TempTableExecShapeReport> CheckAsync(
        IReadOnlyList<TempTableExecShapeCandidate> candidates, CancellationToken cancellationToken = default)
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
            if (candidate.TempTableColumns is not { } tempColumns)
            {
                unanalyzed.Add(new UnanalyzedTempTableExecSite(
                    candidate.TempTableQualifiedName, candidate.ExecutedProcQualifiedName,
                    "the temp table's own declared shape could not be resolved in the catalog",
                    candidate.SourcePath, candidate.Line, candidate.Column));
                continue;
            }

            // A proc with no rows in sys.parameters is either genuinely niladic or doesn't exist
            // at all - the two are indistinguishable from this dictionary alone, but describing
            // it against an empty argument list is safe either way: a real niladic proc describes
            // normally, and a nonexistent one comes back as a real compile error from the engine
            // itself below, caught the same way any other describe failure is.
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

            var kind = WriteLossClassifier.Classify(tempColumn.Type, describedType, sourceExpression: null);
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
