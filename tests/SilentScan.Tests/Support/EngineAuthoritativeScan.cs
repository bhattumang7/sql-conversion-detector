using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Live;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Support;

/// <summary>
/// The engine-authoritative replacement for what used to be a one-line, no-database
/// <c>ScanReportBuilder.BuildFromParseResults(parseResults, collation)</c> call in a pipeline
/// fixture test (roadmap "delete the file-parsed catalog path and the file-only scan pipeline" -
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> no longer builds a catalog from parsed
/// DDL text on its own; every caller must supply one, and the only place that catalog can now
/// come from is a real database). Deploys the fixture SQL to a fresh, GUID-suffixed disposable
/// database (unrestricted <see cref="ScriptDeployer.DeployAsync"/>, not the corpus whitelist -
/// this runs a test's OWN authored fixture text, not third-party corpus DML, so there is nothing
/// to guard against executing), then runs the SAME <see cref="LiveScanRunner"/> a live
/// <c>scan-db</c> target uses: catalog from engine metadata, module bodies from
/// <c>sys.sql_modules</c>. Each call gets its own fresh database and drops it unconditionally,
/// so tests calling this concurrently (xUnit's default) never share state.
/// </summary>
public static class EngineAuthoritativeScan
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    /// <summary>
    /// Deploys <paramref name="sql"/> (DDL and/or module definitions, GO-separated) and returns
    /// the resulting scan report. <paramref name="collation"/>, when supplied, creates the
    /// disposable database WITH that explicit collation - many fixtures migrated from the old
    /// file-parsed pipeline were written assuming a specific manifestDeclaredCollation (typically
    /// to make the varchar-vs-nvarchar rule reachable for a column with no explicit COLLATE of
    /// its own); passing that same collation here preserves the test's original intent instead
    /// of leaving it to whatever the Docker instance's own server-level default happens to be.
    /// </summary>
    public static async Task<ScanReport> ScanAsync(string sql, string? collation = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(sql, collation, cancellationToken);
        return result.Report;
    }

    /// <summary>Same deployment as <see cref="ScanAsync"/>, returning the full <see cref="LiveScanResult"/> (catalog summary, module/parity diagnostics) for tests that need more than just the report.</summary>
    public static async Task<LiveScanResult> RunAsync(string sql, string? collation = null, CancellationToken cancellationToken = default)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName, collationName: collation, cancellationToken: cancellationToken);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(WrapBareStatementsInProcedures(sql), databaseName, cancellationToken);
            return await LiveScanRunner.RunAsync(Options.BuildConnectionString(databaseName), cancellationToken: cancellationToken);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    /// <summary>
    /// Old file-mode fixtures routinely put the predicate under test in a bare top-level
    /// SELECT/INSERT/UPDATE/DELETE/MERGE/EXEC statement, no CREATE PROCEDURE wrapper at all -
    /// exactly how file-mode scanning worked (it analyzed every parsed batch, module or not).
    /// The engine-authoritative pipeline can only ever see what a real target database
    /// persists in <c>sys.sql_modules</c> - a bare ad-hoc batch executes and leaves no trace
    /// there at all (verified directly: deploying one and querying
    /// <c>sys.sql_modules</c> afterward returns zero rows, unconditionally, not a race). Rather
    /// than hand-edit every migrated fixture to add a CREATE PROCEDURE wrapper, this rewrites
    /// every MAXIMAL RUN of consecutive GO-separated batches containing only non-DDL statements
    /// (no statement whose type name starts with Create/Alter/Drop - ScriptDom's own consistent
    /// naming for every schema-shaping statement, matching <c>DdlStatementWhitelist</c>'s own
    /// enumeration) into ONE <c>CREATE PROCEDURE dbo.__SilentScanHarness_N AS BEGIN
    /// &lt;run's batches, GO removed&gt; END</c> - a whole run, not one procedure per bare batch,
    /// because a #temp table created in one bare batch (a common SELECT INTO #x pattern) must
    /// stay visible to a LATER bare batch that reads it back, exactly as it would across
    /// GO-separated batches in one real session; splitting each into its own separate procedure
    /// would silently break that (a #temp table never survives past the CREATE PROCEDURE body
    /// that created it - a real, deployment-verified fact, not an assumption). Harmless to a
    /// fixture that already wraps its own predicate in a real module (that batch already
    /// contains a Create-prefixed statement, so it starts a new, un-merged run boundary).
    /// </summary>
    private static string WrapBareStatementsInProcedures(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("fixture.sql", sql);
        if (parseResult.HasErrors || parseResult.Fragment is not TSqlScript { Batches.Count: > 0 } script)
        {
            // A malformed fixture (deliberately, for a parse-failure test) or one with no
            // batches at all - deploy as-is and let it fail/succeed on its own terms.
            return sql;
        }

        var rewritten = new System.Text.StringBuilder();
        var harnessCounter = 0;
        var pendingRun = new List<string>();

        void FlushRun()
        {
            if (pendingRun.Count == 0)
            {
                return;
            }

            harnessCounter++;
            rewritten.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE PROCEDURE dbo.__SilentScanHarness_{harnessCounter} AS");
            rewritten.AppendLine("BEGIN");
            foreach (var batchText in pendingRun)
            {
                rewritten.AppendLine(batchText);
            }

            rewritten.AppendLine("END");
            rewritten.AppendLine("GO");
            pendingRun.Clear();
        }

        foreach (var batch in script.Batches)
        {
            var batchText = sql.Substring(batch.StartOffset, batch.FragmentLength);
            if (batch.Statements.Count > 0 && batch.Statements.All(IsNonDdlStatement))
            {
                pendingRun.Add(batchText);
                continue;
            }

            FlushRun();
            rewritten.AppendLine(batchText);
            rewritten.AppendLine("GO");
        }

        FlushRun();
        return rewritten.ToString();
    }

    private static bool IsNonDdlStatement(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        return !name.StartsWith("Create", StringComparison.Ordinal)
            && !name.StartsWith("Alter", StringComparison.Ordinal)
            && !name.StartsWith("Drop", StringComparison.Ordinal);
    }

    /// <summary>
    /// Same as <see cref="ScanAsync"/>, for a fixture spread across multiple files on disk (e.g.
    /// <c>fixtures/mini_project/</c>) rather than one inline string - every file is decoded and
    /// concatenated in the same deterministic order <see cref="SqlFileDiscovery"/> already
    /// guarantees, each separated by its own <c>GO</c> so a file that doesn't end in one can
    /// never bleed its last batch into the next file's first.
    /// </summary>
    public static async Task<ScanReport> ScanFilesAsync(IReadOnlyList<string> sqlFilePaths, string? collation = null, CancellationToken cancellationToken = default)
    {
        var combined = string.Join("\nGO\n", sqlFilePaths.Select(SqlScriptParser.DecodeFile));
        return await ScanAsync(combined, collation, cancellationToken);
    }
}
