using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Verify.Deployment;

/// <summary>
/// Deploys a .sql script to the oracle by executing its GO-separated batches sequentially on
/// one connection, so CREATE DATABASE / USE / DDL land in the same session the way sqlcmd
/// would run them (CLAUDE.md Verify: "deploy its DDL to a fresh database").
/// </summary>
public sealed partial class ScriptDeployer
{
    private readonly SqlServerOptions _options;

    public ScriptDeployer(SqlServerOptions options)
    {
        _options = options;
    }

    public async Task DeployAsync(string script, string? initialDatabase = null, CancellationToken cancellationToken = default)
    {
        var batches = GoBatchSplitter.Split(script);

        await using var connection = new SqlConnection(_options.BuildConnectionString(initialDatabase));
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in batches)
        {
            await ExecuteBatchAsync(connection, batch, cancellationToken);
        }
    }

    /// <summary>
    /// The code-level backstop for "corpus DML is never executed, anywhere" (CLAUDE.md hard
    /// scope): parses <paramref name="script"/> with the same ScriptDOM parser the analysis
    /// passes use, and deploys only batches whose every statement is on
    /// <see cref="DdlStatementWhitelist"/> - a batch containing a seed INSERT, a stray EXEC, a
    /// GRANT, or anything else this project has no static-analysis use for is skipped and
    /// reported, never executed. Uses ScriptDOM's own batch segmentation (not
    /// <see cref="GoBatchSplitter"/>'s regex/lexer split) as the single source of truth for
    /// both classification and the exact text executed, so the two can never disagree about
    /// where one batch ends and the next begins. Deployment is best-effort per BATCH, not per
    /// file: one skipped or failed batch does not abandon every later batch in the script -
    /// aborting the whole file on the first one used to silently drop every later CREATE
    /// TABLE/CREATE INDEX too, which is how an index the environment parity gate then needs
    /// (see IndexDeploymentChecker) went missing for reasons unrelated to that index's own DDL.
    /// Returns one message per skipped/failed batch; never throws for a batch-level problem.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeployWhitelistedDdlAsync(
        string script, string? initialDatabase = null, CancellationToken cancellationToken = default)
    {
        var parseResult = SqlScriptParser.ParseText(initialDatabase ?? "corpus-ddl", script);
        var messages = new List<string>();

        // A batch containing a syntax error is dropped by ScriptDOM itself before it ever
        // reaches Batches (same behavior CatalogBuilder/LineageResolver already rely on) - that
        // must still be reported here, or a parse failure silently looks identical to "nothing
        // in this file needed deploying" instead of a real, honestly-accounted gap.
        foreach (var error in parseResult.Errors)
        {
            messages.Add($"parse error at line {error.Line}: {error.Message}");
        }

        if (parseResult.Fragment is not TSqlScript { Batches: { Count: > 0 } scriptBatches })
        {
            return messages;
        }

        await using var connection = new SqlConnection(_options.BuildConnectionString(initialDatabase));
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in scriptBatches)
        {
            var disallowed = DdlStatementWhitelist.DisallowedStatementTypeNames(batch);
            if (disallowed.Count > 0)
            {
                messages.Add($"batch at line {batch.StartLine} skipped - contains non-whitelisted statement kind(s): {string.Join(", ", disallowed)}");
                continue;
            }

            if (batch.Statements.Count == 0)
            {
                continue;
            }

            var batchText = script.Substring(batch.StartOffset, batch.FragmentLength);
            try
            {
                await ExecuteBatchAsync(connection, batchText, cancellationToken);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                messages.Add(ex.Message);
            }
        }

        return messages;
    }

    private static async Task ExecuteBatchAsync(SqlConnection connection, string batch, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = batch;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deploys every whitelisted batch across ALL of <paramref name="scripts"/> together, retrying
    /// whatever failed in earlier passes rather than giving up on the first error - real-world
    /// multi-file corpora (Wide World Importers' Tables/*.sql, one file per table) routinely
    /// declare a foreign key or a DEFAULT NEXT VALUE FOR sequence reference to an object that
    /// only exists because a LATER file in glob order creates it. Splitting deployment into a
    /// table-only pass and a constraint-only pass would need to parse apart inline FK constraints
    /// from CREATE TABLE bodies (nontrivial SQL rewriting, and risky to get subtly wrong); a
    /// blind multi-pass retry gets the same ordering-independence for free, since a batch that
    /// fails on a missing dependency simply succeeds once that dependency exists after a later
    /// batch in the same or a later pass deploys it. Stops once a full pass makes no forward
    /// progress (every remaining batch failed again) or <paramref name="maxPasses"/> is reached,
    /// whichever comes first - a batch that's simply broken (not just late) would otherwise retry
    /// forever. Returns one message per batch that was skipped (whitelist) or that still failed
    /// after every pass, each prefixed with the label of the script it came from.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeployWhitelistedDdlWithRetryAsync(
        IReadOnlyList<(string Label, string Script)> scripts, string? initialDatabase = null, int maxPasses = 5,
        bool allowProcedureAndTriggerDefinitions = false, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var pending = CollectWhitelistedBatches(scripts, allowProcedureAndTriggerDefinitions, messages);

        await using var connection = new SqlConnection(_options.BuildConnectionString(initialDatabase));
        await connection.OpenAsync(cancellationToken);

        var lastFailureByBatch = new Dictionary<(string Label, string BatchText), string>();
        pending = await RunRetryPassesAsync(connection, pending, maxPasses, lastFailureByBatch, cancellationToken);

        messages.AddRange(pending.Select(item => $"{item.Label}: {lastFailureByBatch[item]}"));

        return messages;
    }

    /// <summary>Parses every script, skips (with a message) any batch containing a non-whitelisted statement kind or a parse error, and returns the rest as raw batch text ready to execute.</summary>
    private static List<(string Label, string BatchText)> CollectWhitelistedBatches(
        IReadOnlyList<(string Label, string Script)> scripts, bool allowProcedureAndTriggerDefinitions, List<string> messages)
    {
        var pending = new List<(string Label, string BatchText)>();

        foreach (var (label, script) in scripts)
        {
            var parseResult = SqlScriptParser.ParseText(label, script);
            foreach (var error in parseResult.Errors)
            {
                messages.Add($"{label}: parse error at line {error.Line}: {error.Message}");
            }

            if (parseResult.Fragment is not TSqlScript { Batches: { Count: > 0 } scriptBatches })
            {
                continue;
            }

            foreach (var batch in scriptBatches)
            {
                var disallowed = DdlStatementWhitelist.DisallowedStatementTypeNames(batch, allowProcedureAndTriggerDefinitions);
                if (disallowed.Count > 0)
                {
                    messages.Add($"{label}: batch at line {batch.StartLine} skipped - contains non-whitelisted statement kind(s): {string.Join(", ", disallowed)}");
                    continue;
                }

                if (batch.Statements.Count == 0)
                {
                    continue;
                }

                var batchText = script.Substring(batch.StartOffset, batch.FragmentLength);
                pending.Add((label, RewriteAlterToCreateOrAlter(batch, batchText)));
            }
        }

        return pending;
    }

    /// <summary>Retries whatever failed in earlier passes (see the type-level doc comment for why) until either everything deploys, a full pass makes no forward progress, or <paramref name="maxPasses"/> is reached. <paramref name="lastFailureByBatch"/> is filled in with each still-pending batch's most recent failure message.</summary>
    private static async Task<List<(string Label, string BatchText)>> RunRetryPassesAsync(
        SqlConnection connection, List<(string Label, string BatchText)> pending, int maxPasses,
        Dictionary<(string Label, string BatchText), string> lastFailureByBatch, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < maxPasses && pending.Count > 0; pass++)
        {
            var stillPending = new List<(string Label, string BatchText)>();
            var progressed = false;

            foreach (var item in pending)
            {
                try
                {
                    await ExecuteBatchAsync(connection, item.BatchText, cancellationToken);
                    progressed = true;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    lastFailureByBatch[item] = ex.Message;
                    stillPending.Add(item);
                }
            }

            pending = stillPending;
            if (!progressed)
            {
                break;
            }
        }

        return pending;
    }

    private static readonly HashSet<Type> RewritableAlterStatementTypes =
    [
        typeof(AlterProcedureStatement), typeof(AlterFunctionStatement), typeof(AlterTriggerStatement), typeof(AlterViewStatement),
    ];

    /// <summary>
    /// Real-world corpora routinely guard a proc/function/trigger/view definition with a
    /// dynamic-SQL stub ("<c>IF OBJECT_ID(...) IS NULL EXEC('CREATE PROCEDURE ... AS RETURN
    /// 0')</c>" then an unconditional <c>ALTER PROCEDURE</c> for the real body - First Responder
    /// Kit's every sp_Blitz*.sql file is exactly this shape) specifically so the ALTER always
    /// succeeds whether or not a PRIOR run already created the object. This deployer correctly
    /// never executes that EXEC(...) stub (CLAUDE.md: corpus DML/procs are never executed) - but
    /// that means the stub never runs EITHER, so the object genuinely does not exist yet when
    /// the ALTER that follows tries to target it, and SQL Server rejects it outright ("Invalid
    /// object name"). Rewriting the batch's own leading ALTER keyword to CREATE OR ALTER (only
    /// ever applied when the batch's SOLE statement is confirmed by the parsed AST to be exactly
    /// one of the four alterable kinds, never a blind string search) reproduces the stub
    /// pattern's actual INTENT - "this definition wins, regardless of whether the object
    /// previously existed" - without ever running arbitrary dynamic SQL to get there. A
    /// malformed rewrite (extremely unlikely given the type-gated guard, but not provably
    /// impossible against every real-world file's exact comment placement) fails deployment
    /// exactly like any other bad batch - reported, never silently miscompiled.
    /// </summary>
    private static string RewriteAlterToCreateOrAlter(TSqlBatch batch, string batchText)
    {
        if (batch.Statements is not [{ } soleStatement] || !RewritableAlterStatementTypes.Contains(soleStatement.GetType()))
        {
            return batchText;
        }

        return AlterKeywordPattern().Replace(batchText, "CREATE OR ALTER", 1);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bALTER\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AlterKeywordPattern();
}
