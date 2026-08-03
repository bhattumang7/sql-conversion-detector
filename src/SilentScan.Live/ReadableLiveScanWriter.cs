using System.Globalization;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Live.Catalog;

namespace SilentScan.Live;

/// <summary>
/// Renders a live-database scan for a reader. The findings themselves are laid out exactly as
/// a file scan's are (<see cref="ReadableScanReportWriter"/>); what live mode adds on top is
/// everything that decides whether those findings can be trusted at all - what the connection
/// actually saw, whether the pipeline's inferred view types matched the server's own metadata,
/// which modules had no readable T-SQL body, and, when asked for, which findings the plan cache
/// shows converting right now.
/// </summary>
public static class ReadableLiveScanWriter
{
    /// <summary>
    /// Names the scanned target for the report heading, from the connection string's server and
    /// database only - never the whole connection string, which would put any credentials in it
    /// into a report written to a file and handed to someone else.
    /// </summary>
    public static string DescribeTarget(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrEmpty(builder.DataSource))
            {
                // Nothing recognisable to name it by - including when the string parsed but held
                // no server at all. Naming it generically beats echoing back whatever was passed.
                return string.IsNullOrEmpty(builder.InitialCatalog) ? "the connected database" : builder.InitialCatalog;
            }

            var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "(default database)" : builder.InitialCatalog;
            return $"{builder.DataSource}/{database}";
        }
        catch (ArgumentException)
        {
            return "the connected database";
        }
    }

    public static string Write(LiveScanResult result, string databaseLabel, ReadableStyle style)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<ReadableBlock> blocks = [new ReadableBlock.Heading(1, $"SilentScan live scan - {databaseLabel}")];
        blocks.AddRange(Connection(result));
        blocks.AddRange(LineageParity(result));
        blocks.AddRange(PlanCacheEvidence(result));
        blocks.AddRange(WorkloadFindings(result));

        blocks.AddRange(ReadableScanReportWriter.BuildSections(result.Report, headingLevel: 2));
        blocks.AddRange(UnanalyzableModules(result));

        return ReadableDocumentRenderer.Render(new ReadableDocument(blocks), style);
    }

    private static IEnumerable<ReadableBlock> Connection(LiveScanResult result)
    {
        var catalog = result.CatalogSummary;

        yield return new ReadableBlock.Heading(2, "What was read");
        yield return new ReadableBlock.Paragraph(
            $"Read-only catalog queries only - nothing in the target database was executed. Database collation {catalog.DatabaseCollation}.");
        yield return new ReadableBlock.Table(
            ["Tables", "Columns", "Indexes", "Type aliases", "Modules analyzed"],
            [[
                catalog.TableCount.ToString(CultureInfo.InvariantCulture),
                catalog.ColumnCount.ToString(CultureInfo.InvariantCulture),
                catalog.IndexCount.ToString(CultureInfo.InvariantCulture),
                catalog.TypeAliasCount.ToString(CultureInfo.InvariantCulture),
                result.ModulesAnalyzed.ToString(CultureInfo.InvariantCulture),
            ]]);
    }

    private static IEnumerable<ReadableBlock> LineageParity(LiveScanResult result)
    {
        if (result.LineageParityMismatches.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(2, $"View type mismatches against the server's own metadata ({result.LineageParityMismatches.Count})");
        yield return new ReadableBlock.Paragraph(
            "The type this tool inferred for a view column disagrees with what sys.columns reports. That is a bug in this tool, not in the scanned database, and every finding below that touches one of these columns rests on a type the pipeline got wrong - read them as suspect until this is fixed.");
        yield return new ReadableBlock.Table(
            ["View column", "Facet", "Inferred", "Actual"],
            [.. result.LineageParityMismatches.Select(m => new List<string>
            {
                $"{m.QualifiedViewName}.{m.ColumnName}",
                m.Facet,
                m.InferredValue,
                m.ActualValue,
            })]);
    }

    private static IEnumerable<ReadableBlock> PlanCacheEvidence(LiveScanResult result)
    {
        if (result.PlanCacheEvidence is not { } evidence)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(2, "Confirmed by the live plan cache");

        if (evidence.UnavailableReason is { } reason)
        {
            yield return new ReadableBlock.Paragraph($"The plan cache could not be read ({reason}), so no finding below carries live evidence either way.");
            yield break;
        }

        var observed = result.RankedFindings.Where(f => f.ObservedInLivePlanCache).ToList();
        yield return new ReadableBlock.Paragraph(
            $"{evidence.PlansInspected.ToString(CultureInfo.InvariantCulture)} cached plans were inspected. " +
            (observed.Count == 0
                ? "None of the static findings below show up as an actual conversion in a cached plan - they remain what the predicate makes possible, not what the server is demonstrably doing."
                : $"{observed.Count.ToString(CultureInfo.InvariantCulture)} of the findings below are converting in a plan the server is running right now, ordered by how often that plan has executed."));

        if (observed.Count > 0)
        {
            yield return new ReadableBlock.Table(
                ["Column", "Where", "Executions"],
                [.. observed
                    .OrderByDescending(f => f.ObservedExecutionCount)
                    .ThenBy(f => f.Finding.SourcePath, StringComparer.Ordinal)
                    .Select(f => new List<string>
                    {
                        $"{f.Finding.Column.TableQualifiedName}.{f.Finding.Column.ColumnName}",
                        $"{f.Finding.SourcePath}:{f.Finding.Line.ToString(CultureInfo.InvariantCulture)}",
                        f.ObservedExecutionCount.ToString(CultureInfo.InvariantCulture),
                    })]);
        }
    }

    /// <summary>
    /// Roadmap Phase D: conversions the live plan cache confirms are actually running right now
    /// for a (table, column) pair no module body produced a static finding for at all - the
    /// dominant real-world case being ad-hoc, parameterized application-side SQL (an ORM, a
    /// hand-written data-access layer) that was never a stored procedure. These carry no source
    /// file/line - the query text that produced them was never scanned, only its plan - so they
    /// are reported by table/column and observed cost instead.
    /// </summary>
    private static IEnumerable<ReadableBlock> WorkloadFindings(LiveScanResult result)
    {
        if (result.WorkloadFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(2, $"Conversions observed in the workload, not in any scanned module ({result.WorkloadFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "The live plan cache shows these columns converting in a real, currently-cached query plan, but no CREATE PROCEDURE/VIEW/FUNCTION body this scan read produced a matching finding - almost always ad-hoc, parameterized SQL sent directly from application code rather than a stored procedure. Confirmed by the plan itself, not a static inference.");
        yield return new ReadableBlock.Table(
            ["Column", "Indexed", "Outcome", "Executions"],
            [.. result.WorkloadFindings
                .OrderByDescending(f => f.ExecutionCount)
                .Select(f => new List<string>
                {
                    $"{f.TableQualifiedName}.{f.ColumnName}",
                    f.Indexed ? "yes" : "no",
                    f.Verdict == WorkloadVerdict.ScanForced ? "forces a scan" : "degrades the seek",
                    f.ExecutionCount.ToString(CultureInfo.InvariantCulture),
                })]);
    }

    private static IEnumerable<ReadableBlock> UnanalyzableModules(LiveScanResult result)
    {
        if (result.UnanalyzableModules.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(2, $"Modules with no readable T-SQL body ({result.UnanalyzableModules.Count})");
        yield return new ReadableBlock.Paragraph(
            "These exist in the database and were deliberately not analyzed - there is no T-SQL text to analyze. They are listed rather than dropped so nothing above is read as covering them.");
        yield return new ReadableBlock.Table(
            ["Module", "Type", "Why"],
            [.. result.UnanalyzableModules.Select(m => new List<string>
            {
                m.QualifiedName,
                m.ObjectTypeCode,
                m.Reason == UnanalyzableModuleReason.Encrypted ? "encrypted (WITH ENCRYPTION)" : "backed by a CLR assembly",
            })]);
    }
}
