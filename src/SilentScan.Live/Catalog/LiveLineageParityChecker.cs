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

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var (qualifiedName, relation) in lineage.AllRelations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (qualifiedName is null || lineage.CyclicViews.Contains(qualifiedName))
            {
                continue;
            }

            var actualColumns = await ReadColumnsAsync(connection, qualifiedName, cancellationToken);
            if (actualColumns.Count == 0)
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

    private static async Task<Dictionary<string, ActualColumn>> ReadColumnsAsync(
        SqlConnection connection, string schemaQualifiedObjectName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name AS column_name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@objectName)
            ORDER BY c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        command.Parameters.AddWithValue("@objectName", schemaQualifiedObjectName);

        var columns = new Dictionary<string, ActualColumn>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                columns[name] = new ActualColumn(
                    TypeName: reader.GetString(1),
                    MaxLength: reader.GetInt16(2),
                    Precision: reader.GetByte(3),
                    Scale: reader.GetByte(4),
                    CollationName: await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5));
            }
        }
        catch (SqlException)
        {
            // An invalid/unresolvable object name for OBJECT_ID is not itself a parity mismatch.
        }

        return columns;
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
