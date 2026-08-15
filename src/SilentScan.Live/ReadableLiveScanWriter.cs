using System.Globalization;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;

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
    private const string ColumnHeading = "Column";

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

    public static string Write(LiveScanResult result, string databaseLabel, ReadableStyle style, ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<ReadableBlock> blocks = [new ReadableBlock.Heading(1, $"SilentScan live scan - {databaseLabel}")];
        blocks.AddRange(Connection(result));
        blocks.AddRange(LineageParity(result, verbosity));
        blocks.AddRange(PlanCacheEvidence(result));
        blocks.AddRange(WorkloadFindings(result));

        blocks.AddRange(ReadableScanReportWriter.BuildSections(result.Report, headingLevel: 2, verbosity: verbosity));
        blocks.AddRange(UnanalyzableModules(result, verbosity));

        return ReadableDocumentRenderer.Render(new ReadableDocument(blocks), style);
    }

    /// <summary>Mirrors <c>ReadableScanReportWriter</c>'s own brief-mode pointer line - same wording contract, kept local since this writer's gated sections are live-scan-specific.</summary>
    private static ReadableBlock.Paragraph BriefPointer(int count, string noun) =>
        new($"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")} - not listed individually here; re-run with --verbosity full to see each one.");

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

    private static IEnumerable<ReadableBlock> LineageParity(LiveScanResult result, ReadableVerbosity verbosity)
    {
        var parity = result.LineageParity;

        if (parity.Mismatches.Count > 0)
        {
            // Never gated by verbosity, even in Brief - this is a P0 bug in THIS tool's own
            // inference, not a coverage caveat about the scanned database, and it is the only
            // category that fails the scan (see the exit-code comment in ScanDbCommand).
            yield return new ReadableBlock.Heading(2, $"Column types this tool got wrong ({parity.Mismatches.Count})");
            yield return new ReadableBlock.Paragraph(
                "Verified against the type the server computes for this object right now (sys.dm_exec_describe_first_result_set), not against its cached sys.columns metadata, so this is a genuine inference bug in this tool. Every finding below that touches one of these columns rests on a type the pipeline got wrong - read them as suspect until this is fixed. This is the only category that fails the scan.");
            yield return new ReadableBlock.Table(
                ["View column", "Facet", "Inferred", "Live"],
                [.. parity.Mismatches.Select(m => new List<string> { $"{m.QualifiedViewName}.{m.ColumnName}", m.Facet, m.InferredValue, m.ActualValue })]);
        }

        if (parity.UncompilableObjects.Count > 0)
        {
            yield return new ReadableBlock.Heading(2, $"Objects the server cannot compile ({parity.UncompilableObjects.Count})");
            yield return new ReadableBlock.Paragraph(
                "These views/functions do not currently compile - most often they reference something that no longer exists - so the server itself cannot describe them and nothing in this report covers their columns. That is a condition of the scanned database, not a bug in this tool, so it does not fail the scan. Their cached sys.columns metadata is a fossil from when they last compiled successfully.");
            if (verbosity == ReadableVerbosity.Brief)
            {
                yield return BriefPointer(parity.UncompilableObjects.Count, "object");
            }
            else
            {
                yield return new ReadableBlock.Table(
                    ["Object", "Error", "Message"],
                    [.. parity.UncompilableObjects.Select(u => new List<string> { u.QualifiedViewName, u.ErrorNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), u.ErrorMessage })]);
            }
        }

        if (parity.StaleCachedMetadata.Count > 0)
        {
            yield return new ReadableBlock.Heading(2, $"Objects whose cached metadata is out of date ({parity.StaleCachedMetadata.Count})");
            yield return new ReadableBlock.Paragraph(
                "The server's cached column metadata for these objects disagrees with what it computes for them now - a base column's type changed after the object was created, and SQL Server does not refresh a view's or function's own cached metadata when that happens. This tool's inference agrees with the live answer, so nothing here is a finding - it is a maintenance signal for whoever owns the database (sp_refreshview / sp_refreshsqlmodule). Anything else reading these objects' metadata rather than querying them directly will see the stale type.");
            if (verbosity == ReadableVerbosity.Brief)
            {
                yield return BriefPointer(parity.StaleCachedMetadata.Count, "object");
            }
            else
            {
                yield return new ReadableBlock.Table(
                    [ColumnHeading, "Facet", "Cached", "Live"],
                    [.. parity.StaleCachedMetadata.Select(s => new List<string> { $"{s.QualifiedViewName}.{s.ColumnName}", s.Facet, s.CachedValue, s.LiveValue })]);
            }
        }

        if (parity.Unverified.Count > 0)
        {
            yield return new ReadableBlock.Heading(2, $"Columns that could not be live-verified ({parity.Unverified.Count})");
            yield return new ReadableBlock.Paragraph(
                "This tool's inference disagrees with the object's cached sys.columns metadata, but the object could not be verified against a live answer - listed rather than dropped so nothing above is read as covering them.");
            if (verbosity == ReadableVerbosity.Brief)
            {
                yield return BriefPointer(parity.Unverified.Count, "column");
            }
            else
            {
                yield return new ReadableBlock.Table(
                    [ColumnHeading, "Why", "Inferred", "Cached"],
                    [.. parity.Unverified.Select(u => new List<string> { $"{u.QualifiedViewName}.{u.ColumnName}", u.Reason, u.InferredValue, u.CachedValue })]);
            }
        }
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
                [ColumnHeading, "Where", "Executions"],
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
            [ColumnHeading, "Indexed", "Outcome", "Executions"],
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

    private static IEnumerable<ReadableBlock> UnanalyzableModules(LiveScanResult result, ReadableVerbosity verbosity)
    {
        if (result.UnanalyzableModules.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(2, $"Modules with no readable T-SQL body ({result.UnanalyzableModules.Count})");
        yield return new ReadableBlock.Paragraph(
            "These exist in the database and were deliberately not analyzed - there is no T-SQL text to analyze. They are listed rather than dropped so nothing above is read as covering them.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(result.UnanalyzableModules.Count, "module");
            yield break;
        }

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
