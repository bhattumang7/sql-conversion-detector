using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// The live-mode counterpart to <c>SilentScan.Verify.Oracle.LineageParityChecker</c>: diffs every
/// resolved view/TVF's statically-inferred column type against ground truth. For a view or inline
/// TVF (<c>V</c>/<c>IF</c>), ground truth is what the engine computes for that object RIGHT NOW
/// (<c>sys.dm_exec_describe_first_result_set</c>, via <see cref="LiveDescribedColumnReader"/>) -
/// never its cached <c>sys.columns</c> metadata. SQL Server snapshots a view's/inline-TVF's own
/// column metadata at CREATE/ALTER time and never refreshes it when an upstream base column is
/// later retyped (short of <c>sp_refreshview</c>/<c>sp_refreshsqlmodule</c>), so a plain
/// cached-metadata diff conflates three different things: this tool's inference disagreeing with
/// the live answer (a genuine bug), the cache disagreeing with the live answer while this tool's
/// inference agrees with it (a stale cache, not a tool bug), and an object that can no longer
/// compile at all (a database condition, not a tool bug). Base tables (<c>U</c>) and
/// multi-statement TVFs (<c>TF</c>) are exempt from live probing: a base table's <c>sys.columns</c>
/// IS its definition, and a multi-statement TVF's shape is its own authored
/// <c>RETURNS @t TABLE(...)</c> clause - both are read from one source, so staleness is
/// structurally impossible for them, and the plain cached-metadata diff stays correct as-is.
///
/// The corpus oracle's own <c>LineageParityChecker</c> is deliberately left doing the plain
/// cached-metadata diff: it runs inside a freshly-provisioned disposable database immediately
/// after deploying the corpus DDL, where nothing has been ALTERed since, so staleness there is
/// structurally impossible too.
///
/// Self-contained rather than depending on SilentScan.Verify's reader shape (a connection string,
/// not a <c>SqlServerOptions</c>/database-name pair, is this project's only way to reach a
/// database - decomposing an arbitrary connection string back into host/port/user/password to
/// reuse Verify's reader would be lossy for anything but the simplest auth mode).
/// </summary>
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
        // Every relation this gate will actually diff, resolved up front so the read below can
        // discard the (far larger) set of columns belonging to objects lineage never resolved,
        // without materializing them. Cyclic views are excluded here rather than mid-loop: their
        // inferred types are meaningless, so fetching their columns would be wasted work.
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

    /// <summary>
    /// Live-describes every wanted view (one batched round trip) and inline TVF (one round trip
    /// each, needing its own synthesized argument list) - split out of <see cref="CheckAsync"/>
    /// purely to keep that method's own cognitive complexity readable; the classification loop
    /// below is what actually decides what a described (or unrenderable) result means.
    /// </summary>
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
                // Not every resolved relation is a real server object (derived tables, MSTVFs
                // that never became one) - absence here is not itself a mismatch.
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

        // Not a V/IF object at all (base table or multi-statement TVF) - its cached
        // sys.columns/authored shape IS ground truth, exactly as before this class began
        // live-probing anything.
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

    /// <summary>The four outcome lists <see cref="Classify"/> accumulates into before sorting them into the returned <see cref="LiveLineageParityReport"/> - one mutable accumulator instead of threading four lists plus a dedup set through every classification call.</summary>
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

    /// <summary>
    /// Reads every relevant object's columns AND object type code in ONE round trip, keyed by the
    /// same <c>schema.object</c> form <see cref="SchemaObjectNameHelper.Qualify"/> produces for
    /// lineage keys - the object type code decides whether an object is live-probed at all (only
    /// <c>V</c>/<c>IF</c> are). This used to be a per-relation <c>OBJECT_ID(@objectName)</c> query
    /// issued sequentially on a single connection - on a database with thousands of views that is
    /// thousands of serial round trips, so the gate's wall-clock was dominated by network latency
    /// rather than by any work. Rows for objects lineage did not resolve are skipped as they
    /// stream past, so peak memory tracks the number of resolved relations, not the number of
    /// columns in the database.
    /// </summary>
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

    /// <summary>
    /// Compares category first, then collation for a string-family type - the one comparison
    /// implementation shared by all three facet diffs this gate makes (inferred-vs-live,
    /// inferred-vs-cached for non-probed objects, cached-vs-live for staleness detection), so
    /// they can never drift from one another. Null means no disagreement.
    /// </summary>
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
