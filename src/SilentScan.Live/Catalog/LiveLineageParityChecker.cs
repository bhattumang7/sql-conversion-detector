using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Verify.Catalog;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Live.Catalog;

public sealed class LiveLineageParityChecker
{
    private readonly string _connectionString;

    public LiveLineageParityChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<LiveLineageParityReport> CheckAsync(
        LineageCatalog lineage, CancellationToken cancellationToken = default)
    {

        var wanted = lineage.AllRelations.Keys
            .Where(name => name is not null && !lineage.CyclicViews.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return LiveLineageParityReport.Empty;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var actualByObject = await ReadAllColumnsAsync(connection, wanted, cancellationToken);
        var (described, unrenderable) = await DescribeProbeableObjectsAsync(connection, wanted, actualByObject, cancellationToken);

        return Classify(lineage, actualByObject, described, unrenderable);
    }

    private static async Task<(Dictionary<string, DescribedObject> Described, Dictionary<string, string> Unrenderable)> DescribeProbeableObjectsAsync(
        SqlConnection connection, HashSet<string> wanted, Dictionary<string, ActualObject> actualByObject, CancellationToken cancellationToken)
    {
        var described = new Dictionary<string, DescribedObject>(StringComparer.OrdinalIgnoreCase);
        var unrenderable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var probeableViews = wanted.Where(name => actualByObject.TryGetValue(name, out var o) && o.TypeCode == "V").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (probeableViews.Count > 0)
        {
            foreach (var (name, result) in await LiveDescribedColumnReader.DescribeViewsAsync(connection, cancellationToken))
            {
                if (probeableViews.Contains(name))
                {
                    described[name] = result;
                }
            }
        }

        var probeableFunctions = wanted.Where(name => actualByObject.TryGetValue(name, out var o) && o.TypeCode == "IF").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (probeableFunctions.Count > 0)
        {
            var parametersByObject = await LiveDescribedColumnReader.ReadFunctionParametersAsync(connection, cancellationToken);
            foreach (var name in probeableFunctions.OrderBy(n => n, StringComparer.Ordinal))
            {
                var parameters = parametersByObject.TryGetValue(name, out var p) ? p : [];
                var (probe, reason) = LiveDescribeProbeBuilder.BuildFunctionProbe(name, parameters);
                if (probe is null)
                {
                    unrenderable[name] = reason!;
                    continue;
                }

                described[name] = await LiveDescribedColumnReader.DescribeFunctionAsync(connection, probe, cancellationToken);
            }
        }

        return (described, unrenderable);
    }

    private static LiveLineageParityReport Classify(
        LineageCatalog lineage, Dictionary<string, ActualObject> actualByObject,
        Dictionary<string, DescribedObject> described, Dictionary<string, string> unrenderable)
    {
        var buckets = new ParityBuckets();

        foreach (var (qualifiedName, relation) in lineage.AllRelations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (qualifiedName is null || !actualByObject.TryGetValue(qualifiedName, out var actualObject))
            {

                continue;
            }

            foreach (var column in relation.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                ClassifyColumn(qualifiedName, column, actualObject, described, unrenderable, buckets);
            }
        }

        return buckets.ToReport();
    }

    private static void ClassifyColumn(
        string qualifiedName, ResolvedColumn column, ActualObject actualObject,
        Dictionary<string, DescribedObject> described, Dictionary<string, string> unrenderable, ParityBuckets buckets)
    {
        var inferredType = ColumnProvenanceAnalysis.TryGetScalarType(column.Provenance);
        if (inferredType is null)
        {
            return;
        }

        actualObject.Columns.TryGetValue(column.Name, out var cached);

        if (described.TryGetValue(qualifiedName, out var describedObject))
        {
            ClassifyProbedColumn(qualifiedName, column.Name, inferredType, cached, describedObject, buckets);
            return;
        }

        if (unrenderable.TryGetValue(qualifiedName, out var reason))
        {
            if (cached is not null && CompareFacets(inferredType, cached) is { } disagreement)
            {
                buckets.Unverified.Add(new LiveLineageUnverifiedColumn(qualifiedName, column.Name, reason, disagreement.InferredValue, disagreement.ActualValue));
            }

            return;
        }

        if (cached is not null && CompareFacets(inferredType, cached) is { } cachedDisagreement)
        {
            buckets.Mismatches.Add(new LiveLineageParityMismatch(qualifiedName, column.Name, cachedDisagreement.Facet, cachedDisagreement.InferredValue, cachedDisagreement.ActualValue));
        }
    }

    private static void ClassifyProbedColumn(
        string qualifiedName, string columnName, SqlType inferredType, ActualColumn? cached, DescribedObject describedObject, ParityBuckets buckets)
    {
        if (describedObject.IsError)
        {
            if (buckets.ReportedUncompilable.Add(qualifiedName))
            {
                buckets.Uncompilable.Add(new LiveLineageUncompilableObject(qualifiedName, describedObject.ErrorNumber, describedObject.ErrorMessage ?? ""));
            }

            return;
        }

        if (!describedObject.Columns!.TryGetValue(columnName, out var live))
        {
            var cachedValue = cached is not null ? Describe(cached) : "(not in sys.columns either)";
            buckets.Unverified.Add(new LiveLineageUnverifiedColumn(qualifiedName, columnName, "column not present in the live-described result set", Describe(inferredType), cachedValue));
            return;
        }

        if (CompareFacets(inferredType, live) is { } liveDisagreement)
        {
            buckets.Mismatches.Add(new LiveLineageParityMismatch(qualifiedName, columnName, liveDisagreement.Facet, liveDisagreement.InferredValue, liveDisagreement.ActualValue));
            return;
        }

        if (cached is not null && CompareFacets(inferredType, cached) is { } cachedDisagreement)
        {
            buckets.Stale.Add(new LiveLineageStaleMetadata(qualifiedName, columnName, cachedDisagreement.Facet, cachedDisagreement.ActualValue, Describe(live)));
        }
    }

    private sealed class ParityBuckets
    {
        public List<LiveLineageParityMismatch> Mismatches { get; } = [];

        public List<LiveLineageStaleMetadata> Stale { get; } = [];

        public List<LiveLineageUncompilableObject> Uncompilable { get; } = [];

        public List<LiveLineageUnverifiedColumn> Unverified { get; } = [];

        public HashSet<string> ReportedUncompilable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public LiveLineageParityReport ToReport() => new(
            [.. Mismatches.OrderBy(m => m.QualifiedViewName, StringComparer.Ordinal).ThenBy(m => m.ColumnName, StringComparer.Ordinal)],
            [.. Stale.OrderBy(m => m.QualifiedViewName, StringComparer.Ordinal).ThenBy(m => m.ColumnName, StringComparer.Ordinal)],
            [.. Uncompilable.OrderBy(m => m.QualifiedViewName, StringComparer.Ordinal)],
            [.. Unverified.OrderBy(m => m.QualifiedViewName, StringComparer.Ordinal).ThenBy(m => m.ColumnName, StringComparer.Ordinal)]);
    }

    private static async Task<Dictionary<string, ActualObject>> ReadAllColumnsAsync(
        SqlConnection connection, HashSet<string> wanted, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type,
                   c.name AS column_name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE o.is_ms_shipped = 0
            ORDER BY o.object_id, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var byObject = new Dictionary<string, ActualObject>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!wanted.Contains(qualifiedName))
            {
                continue;
            }

            if (!byObject.TryGetValue(qualifiedName, out var actualObject))
            {
                actualObject = new ActualObject(reader.GetString(2).Trim(), new Dictionary<string, ActualColumn>(StringComparer.OrdinalIgnoreCase));
                byObject[qualifiedName] = actualObject;
            }

            actualObject.Columns[reader.GetString(3)] = new ActualColumn(
                TypeName: reader.GetString(4),
                MaxLength: reader.GetInt16(5),
                Precision: reader.GetByte(6),
                Scale: reader.GetByte(7),
                CollationName: await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8));
        }

        return byObject;
    }

    private static (string Facet, string InferredValue, string ActualValue)? CompareFacets(SqlType inferredType, ActualColumn actual)
    {
        var mappedCategory = LiveTypeMapper.Map(actual.TypeName);
        if (mappedCategory != inferredType.Category)
        {
            return ("category", inferredType.Category.ToString(), actual.TypeName);
        }

        if (inferredType.IsStringFamily && inferredType.Collation is not null
            && !string.Equals(inferredType.Collation.Name, actual.CollationName, StringComparison.OrdinalIgnoreCase))
        {
            return ("collation", inferredType.Collation.Name, actual.CollationName ?? "(null)");
        }

        return null;
    }

    private static string Describe(SqlType type) => type.Category.ToString();

    private static string Describe(ActualColumn column) => column.TypeName;

    private sealed record ActualObject(string TypeCode, Dictionary<string, ActualColumn> Columns);

    public sealed record ActualColumn(string TypeName, short MaxLength, byte Precision, byte Scale, string? CollationName);
}
