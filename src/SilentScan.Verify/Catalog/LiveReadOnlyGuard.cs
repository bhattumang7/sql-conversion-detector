using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Verify.Catalog;

public static class LiveReadOnlyGuard
{
public const int DefaultCommandTimeoutSeconds = 300;

    public static void AssertSelectOnly(string sql)
    {
        var statements = ParseSingleBatch(sql);
        var disallowed = statements.FirstOrDefault(s => s is not SelectStatement);
        if (disallowed is not null)
        {
            throw new InvalidOperationException(
                $"Live scanning issues read-only SELECT queries only - refusing to execute a {disallowed.GetType().Name}.");
        }
    }

public static void AssertDescribeFirstResultSetProbeOnly(string sql)
    {
        var statements = ParseSingleBatch(sql);
        var disallowed = statements.FirstOrDefault(s => s is not SelectStatement
            && s is not ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference });
        if (disallowed is not null)
        {
            throw new InvalidOperationException(
                "Describe-first-result-set probes accept only a bare SELECT or a bare named-procedure EXEC - " +
                $"refusing to describe a {disallowed.GetType().Name}.");
        }
    }

    private static IEnumerable<TSqlStatement> ParseSingleBatch(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("live-query", sql);
        if (parseResult.HasErrors || parseResult.Fragment is not TSqlScript { Batches: var batches })
        {
            throw new InvalidOperationException($"Live query failed to parse as valid T-SQL: {sql}");
        }

        return batches.SelectMany(b => b.Statements);
    }

public static SqlCommand CreateReadOnlyCommand(
        this SqlConnection connection,
        string commandText,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        AssertSelectOnly(commandText);
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }
}
