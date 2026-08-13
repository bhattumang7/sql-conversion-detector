using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// The live-mode counterpart to <c>SilentScan.Verify.Oracle.LineageParityChecker</c>: diffs
/// every resolved view/TVF's statically-inferred column type against what <c>sys.columns</c>
/// says that same object's columns actually are - CLAUDE.md's "any mismatch is a P0 lineage
/// bug" environment parity gate, running on every live scan instead of only inside the Verify
/// workflow's disposable oracle databases. Self-contained rather than depending on
/// SilentScan.Verify (a connection string, not a <c>SqlServerOptions</c>/database-name pair,
/// is this project's only way to reach a database - decomposing an arbitrary connection string
/// back into host/port/user/password to reuse Verify's reader would be lossy for anything but
/// the simplest auth mode).
/// </summary>
public sealed class LiveLineageParityChecker
{
    private readonly string _connectionString;

    public LiveLineageParityChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<LiveLineageParityMismatch>> CheckAsync(
        LineageCatalog lineage, CancellationToken cancellationToken = default)
    {
        var mismatches = new List<LiveLineageParityMismatch>();

        // Every relation this gate will actually diff, resolved up front so the read below can
        // discard the (far larger) set of columns belonging to objects lineage never resolved,
        // without materializing them. Cyclic views are excluded here rather than mid-loop: their
        // inferred types are meaningless, so fetching their columns would be wasted work.
        var wanted = lineage.AllRelations.Keys
            .Where(name => name is not null && !lineage.CyclicViews.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return mismatches;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var actualByObject = await ReadAllColumnsAsync(connection, wanted, cancellationToken);

        foreach (var (qualifiedName, relation) in lineage.AllRelations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (qualifiedName is null || !actualByObject.TryGetValue(qualifiedName, out var actualColumns))
            {
                // Not every resolved relation is a real server object (derived tables, MSTVFs
                // that never became one) - absence here is not itself a mismatch.
                continue;
            }

            foreach (var column in relation.Columns)
            {
                var inferredType = ColumnProvenanceAnalysis.TryGetScalarType(column.Provenance);
                if (inferredType is null)
                {
                    continue;
                }

                if (!actualColumns.TryGetValue(column.Name, out var actual))
                {
                    continue;
                }

                CheckColumn(qualifiedName, column.Name, inferredType, actual, mismatches);
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Reads every relevant object's columns in ONE round trip, keyed by the same
    /// <c>schema.object</c> form <see cref="SchemaObjectNameHelper.Qualify"/> produces for
    /// lineage keys. This used to be a per-relation <c>OBJECT_ID(@objectName)</c> query issued
    /// sequentially on a single connection - on a database with thousands of views that is
    /// thousands of serial round trips, so the gate's wall-clock was dominated by network
    /// latency rather than by any work. Rows for objects lineage did not resolve are skipped as
    /// they stream past, so peak memory tracks the number of resolved relations, not the number
    /// of columns in the database.
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, ActualColumn>>> ReadAllColumnsAsync(
        SqlConnection connection, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name,
                   c.name AS column_name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE o.is_ms_shipped = 0
            ORDER BY o.object_id, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var byObject = new Dictionary<string, Dictionary<string, ActualColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!wanted.Contains(qualifiedName))
            {
                continue;
            }

            if (!byObject.TryGetValue(qualifiedName, out var columns))
            {
                columns = new Dictionary<string, ActualColumn>(StringComparer.OrdinalIgnoreCase);
                byObject[qualifiedName] = columns;
            }

            columns[reader.GetString(2)] = new ActualColumn(
                TypeName: reader.GetString(3),
                MaxLength: reader.GetInt16(4),
                Precision: reader.GetByte(5),
                Scale: reader.GetByte(6),
                CollationName: await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7));
        }

        return byObject;
    }

    private static void CheckColumn(string qualifiedName, string columnName, SqlType inferredType, ActualColumn actual, List<LiveLineageParityMismatch> mismatches)
    {
        var mappedCategory = LiveTypeMapper.Map(actual.TypeName);
        if (mappedCategory != inferredType.Category)
        {
            mismatches.Add(new LiveLineageParityMismatch(qualifiedName, columnName, "category", inferredType.Category.ToString(), actual.TypeName));
            return;
        }

        if (inferredType.IsStringFamily && inferredType.Collation is not null
            && !string.Equals(inferredType.Collation.Name, actual.CollationName, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add(new LiveLineageParityMismatch(qualifiedName, columnName, "collation", inferredType.Collation.Name, actual.CollationName ?? "(null)"));
        }
    }

    private sealed record ActualColumn(string TypeName, short MaxLength, byte Precision, byte Scale, string? CollationName);
}

/// <summary>One inferred-vs-actual disagreement the live parity gate found - CLAUDE.md: "any mismatch is a P0 lineage bug".</summary>
public sealed record LiveLineageParityMismatch(string QualifiedViewName, string ColumnName, string Facet, string InferredValue, string ActualValue);
