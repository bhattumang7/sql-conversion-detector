using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Reporting;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live;

public static class LiveScanRunner
{
    public static async Task<LiveScanResult> RunAsync(
        string connectionString,
        bool includePlanCacheEvidence = false,
        FindingConfidence minimumConfidence = FindingConfidence.High,
        IScanProgress? progress = null,
        bool fetchSqlFromTables = false,
        CancellationToken cancellationToken = default)
    {
        progress ??= NullScanProgress.Instance;

        DatabaseCatalog catalog;
        using (var catalogStage = progress.Begin("reading catalog"))
        {
            catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
            catalogStage.Complete($"{catalog.Tables.Count:N0} tables, {catalog.Tables.Sum(t => t.Columns.Count):N0} columns");
        }

        LiveModuleReadResult moduleResult;
        using (var moduleStage = progress.Begin("reading modules"))
        {
            moduleResult = await new LiveModuleReader(connectionString).ReadAsync(cancellationToken);
            moduleStage.Complete($"{moduleResult.Modules.Count:N0} modules");
        }

        var modules = moduleResult.Modules;
        var moduleCount = modules.Count;
        var unanalyzable = moduleResult.Unanalyzable;

        foreach (var module in modules)
        {
            catalog.AddModuleUsesQuotedIdentifier(module.QualifiedName, module.UsesQuotedIdentifier);
            catalog.AddModuleUsesAnsiNulls(module.QualifiedName, module.UsesAnsiNulls);
            catalog.AddModuleIsSchemaBound(module.QualifiedName, module.IsSchemaBound);
            catalog.AddModuleIsRecompiled(module.QualifiedName, module.IsRecompiled);
            catalog.AddModuleUsesDatabaseCollation(module.QualifiedName, module.UsesDatabaseCollation);
        }

        IEnumerable<SqlParseResult> parseResultSource() =>
            modules.AsParallel().AsOrdered()
                .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier, catalog.CompatibilityLevel));

        using (var extrasStage = progress.Begin("merging module-body catalog extras"))
        {
            catalog.MergeFileModeExtras(CatalogBuilder.Build(parseResultSource(), catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name, catalog.IsAnsiNullDefaultOn));
            extrasStage.Complete($"{catalog.Tables.Count:N0} tables");
        }
        PhaseMemory.ReleaseBetweenPhases();

        using (var dynamicExtrasStage = progress.Begin("discovering dynamic-SQL temp tables"))
        {
            var discovered = DynamicSqlTempTableDiscovery.Discover(
                parseResultSource(), catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name, catalog.CompatibilityLevel, catalog);
            catalog.MergeFileModeExtras(discovered);
            dynamicExtrasStage.Complete($"{catalog.Tables.Count:N0} tables");
        }
        PhaseMemory.ReleaseBetweenPhases();

        LineageCatalog lineage;
        using (var lineageStage = progress.Begin("resolving lineage"))
        {
            lineage = LineageResolver.Resolve(catalog, parseResultSource());
            lineageStage.Complete($"{lineage.AllRelations.Count:N0} relations");
        }
        PhaseMemory.ReleaseBetweenPhases();

        LiveLineageParityReport parity;
        using (var parityStage = progress.Begin("checking live parity"))
        {
            parity = await new LiveLineageParityChecker(connectionString).CheckAsync(lineage, cancellationToken);
            parityStage.Complete(
                $"{parity.Mismatches.Count:N0} mismatches, {parity.UncompilableObjects.Count:N0} uncompilable, " +
                $"{parity.StaleCachedMetadata.Count:N0} stale, {parity.Unverified.Count:N0} unverified");
        }

        ScanReport report;
        if (fetchSqlFromTables)
        {
            await using var fetchConnection = new SqlConnection(connectionString);
            await fetchConnection.OpenAsync(cancellationToken);
            var rowValueFetcher = new LiveTableRowValueFetcher(fetchConnection);
            report = ScanReportBuilder.BuildFromParseResults(
                parseResultSource(), catalog: catalog, minimumConfidence: minimumConfidence, resolvedLineage: lineage, progress: progress,
                rowValueFetcher: rowValueFetcher);
        }
        else
        {
            report = ScanReportBuilder.BuildFromParseResults(
                parseResultSource(), catalog: catalog, minimumConfidence: minimumConfidence, resolvedLineage: lineage, progress: progress);
        }

        TempTableExecShapeReport tempTableExecShape;
        using (var tempTableStage = progress.Begin("checking INSERT...EXEC temp-table shapes"))
        {
            var candidates = parseResultSource()
                .SelectMany(r => TempTableExecShapeCandidateScanner.Scan(r, catalog))
                .ToList();
            tempTableExecShape = await new TempTableExecShapeChecker(connectionString).CheckAsync(candidates, cancellationToken);
            var filteredTempTableFindings = tempTableExecShape.Findings.Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("TempTableExecShapeScanner", filteredTempTableFindings);
            tempTableStage.Complete($"{filteredTempTableFindings.Count:N0} findings, {tempTableExecShape.Unanalyzed.Count:N0} unanalyzed");
        }
        PhaseMemory.ReleaseBetweenPhases();

        using (var databaseConfigStage = progress.Begin("reading database-level configuration flags"))
        {
            var databaseConfigFindings = (await new DatabaseConfigurationReader(connectionString).ReadAsync(cancellationToken))
                .Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("DatabaseConfigurationScanner", databaseConfigFindings);
            databaseConfigStage.Complete($"{databaseConfigFindings.Count:N0} findings");
        }

        using (var forcedParamStage = progress.Begin("checking forced-parameterization-defeating query shapes"))
        {
            var isParameterizationForced = await new DatabaseConfigurationReader(connectionString)
                .ReadIsParameterizationForcedAsync(cancellationToken);
            var forcedParameterizationFindings = isParameterizationForced
                ? ForcedParameterizationScanner.Scan(parseResultSource()).Where(f => f.Confidence <= minimumConfidence).ToList()
                : [];
            report = report.WithFindings("ForcedParameterizationScanner", forcedParameterizationFindings);
            forcedParamStage.Complete($"{forcedParameterizationFindings.Count:N0} findings");
        }

        using (var danglingReferenceStage = progress.Begin("checking for references to nonexistent objects"))
        {
            var danglingObjectReferenceFindings = (await new DanglingObjectReferenceChecker(connectionString).CheckAsync(cancellationToken))
                .Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("DanglingObjectReferenceScanner", danglingObjectReferenceFindings);
            danglingReferenceStage.Complete($"{danglingObjectReferenceFindings.Count:N0} findings");
        }

        using (var indexDesignStage = progress.Begin("checking clustered/heap index design"))
        {

            var dmlTargetTables = DmlTargetTableScanner.Scan(parseResultSource(), catalog);
            var indexDesignFindings = IndexDesignScanner.Scan(catalog, dmlTargetTables).Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("IndexDesignScanner", indexDesignFindings);
            indexDesignStage.Complete($"{indexDesignFindings.Count:N0} findings");
        }

        using (var identityRangeStage = progress.Begin("checking identity/sequence range"))
        {
            var identityRangeFindings = IdentityRangeScanner.Scan(catalog).Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("IdentityRangeScanner", identityRangeFindings);
            identityRangeStage.Complete($"{identityRangeFindings.Count:N0} findings");
        }

        using (var staleSelectStarViewStage = progress.Begin("checking SELECT * view staleness against base tables"))
        {
            var (views, _) = ViewDefinitionExtractor.Extract(parseResultSource(), catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
            var staleSelectStarViewFindings = StaleSelectStarViewScanner.Scan(views, catalog).Where(f => f.Confidence <= minimumConfidence).ToList();
            report = report.WithFindings("StaleSelectStarViewScanner", staleSelectStarViewFindings);
            staleSelectStarViewStage.Complete($"{staleSelectStarViewFindings.Count:N0} findings");
        }

        PlanCacheEvidenceResult? planCacheEvidence = null;
        IReadOnlyList<RankedFinding> rankedFindings = [];
        IReadOnlyList<WorkloadFinding> workloadFindings = [];
        if (includePlanCacheEvidence)
        {
            var planCacheReader = new LivePlanCacheReader(connectionString);
            planCacheEvidence = await planCacheReader.ReadObservedConversionsAsync(cancellationToken: cancellationToken);
            var typedFindings = report.Find<TypedPredicateFinding>("TypedPredicateExtractor");
            rankedFindings = RankByPlanCacheEvidence(typedFindings, planCacheEvidence);

            var alreadyCovered = typedFindings
                .Select(f => (f.Column.TableQualifiedName, f.Column.ColumnName))
                .ToHashSet(TupleOrdinalIgnoreCaseComparer.Instance);
            workloadFindings = await planCacheReader.ReadWorkloadFindingsAsync(catalog, alreadyCovered, cancellationToken: cancellationToken);
        }

        return new LiveScanResult(
            report, LiveCatalogSummary.From(catalog), moduleCount, parity,
            unanalyzable, planCacheEvidence, rankedFindings, workloadFindings, tempTableExecShape);
    }

    private static List<RankedFinding> RankByPlanCacheEvidence(
        IReadOnlyList<TypedPredicateFinding> findings, PlanCacheEvidenceResult evidence)
    {

        return findings
            .Select(f =>
            {
                var observed = evidence.TryGetExecutionCount(f.Column.TableQualifiedName, f.Column.ColumnName, out var count);
                return new RankedFinding(f, observed, count);
            })
            .OrderByDescending(r => r.ObservedInLivePlanCache)
            .ThenByDescending(r => r.ObservedExecutionCount)
            .ToList();
    }
}

public sealed record LiveScanResult(
    ScanReport Report,
    LiveCatalogSummary CatalogSummary,
    int ModulesAnalyzed,
    LiveLineageParityReport LineageParity,
    IReadOnlyList<UnanalyzableModule> UnanalyzableModules,
    PlanCacheEvidenceResult? PlanCacheEvidence,
    IReadOnlyList<RankedFinding> RankedFindings,
    IReadOnlyList<WorkloadFinding> WorkloadFindings,
    TempTableExecShapeReport TempTableExecShape);

public sealed record RankedFinding(TypedPredicateFinding Finding, bool ObservedInLivePlanCache, long ObservedExecutionCount);

internal sealed class TupleOrdinalIgnoreCaseComparer : IEqualityComparer<(string TableQualifiedName, string ColumnName)>
{
    public static readonly TupleOrdinalIgnoreCaseComparer Instance = new();

    public bool Equals((string TableQualifiedName, string ColumnName) x, (string TableQualifiedName, string ColumnName) y) =>
        string.Equals(x.TableQualifiedName, y.TableQualifiedName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.ColumnName, y.ColumnName, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string TableQualifiedName, string ColumnName) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableQualifiedName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ColumnName));
}
