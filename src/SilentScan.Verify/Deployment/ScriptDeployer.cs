using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Verify.Deployment;

/// <summary>
/// Deploys a .sql script to the oracle by executing its GO-separated batches sequentially on
/// one connection, so CREATE DATABASE / USE / DDL land in the same session the way sqlcmd
/// would run them (CLAUDE.md Verify: "deploy its DDL to a fresh database").
/// </summary>
public sealed class ScriptDeployer
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
}
