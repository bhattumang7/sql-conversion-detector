using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Verify.Deployment;

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

    public async Task<IReadOnlyList<string>> DeployWhitelistedDdlAsync(
        string script, string? initialDatabase = null, CancellationToken cancellationToken = default)
    {
        var parseResult = SqlScriptParser.ParseText(initialDatabase ?? "corpus-ddl", script);
        var messages = new List<string>();

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

    public async Task<IReadOnlyList<string>> DeployWhitelistedDdlWithRetryAsync(
        IReadOnlyList<(string Label, string Script)> scripts, string? initialDatabase = null, int maxPasses = 5,
        bool allowProcedureAndTriggerDefinitions = false, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var pending = CollectWhitelistedBatches(scripts, allowProcedureAndTriggerDefinitions, messages);

        await using var connection = new SqlConnection(_options.BuildConnectionString(initialDatabase));
        await connection.OpenAsync(cancellationToken);

        var lastFailureByBatch = new Dictionary<(string Label, string BatchText), string>();
        var connectionState = new SharedConnectionSetState();
        pending = await RunRetryPassesAsync(connection, pending, maxPasses, lastFailureByBatch, connectionState, cancellationToken);

        messages.AddRange(pending.Select(item => $"{item.Label}: {lastFailureByBatch[item]}"));

        return messages;
    }

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

    private static async Task<List<(string Label, string BatchText)>> RunRetryPassesAsync(
        SqlConnection connection, List<(string Label, string BatchText)> pending, int maxPasses,
        Dictionary<(string Label, string BatchText), string> lastFailureByBatch, SharedConnectionSetState connectionState, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < maxPasses && pending.Count > 0; pass++)
        {
            var stillPending = new List<(string Label, string BatchText)>();
            var progressed = false;

            foreach (var item in pending)
            {
                try
                {

                    await connectionState.ResetToDefaultsIfNewFileAsync(connection, item.Label, cancellationToken);
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

    private sealed class SharedConnectionSetState
    {
        private string? _lastExecutedLabel;

        public async Task ResetToDefaultsIfNewFileAsync(SqlConnection connection, string label, CancellationToken cancellationToken)
        {
            if (_lastExecutedLabel == label)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            _lastExecutedLabel = label;
        }
    }

    private static readonly HashSet<Type> RewritableAlterStatementTypes =
    [
        typeof(AlterProcedureStatement), typeof(AlterFunctionStatement), typeof(AlterTriggerStatement), typeof(AlterViewStatement),
    ];

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
