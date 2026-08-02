using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Live.Catalog;

/// <summary>
/// The code-level backstop for "a connected live database is scanned read-only" (CLAUDE.md hard
/// scope): every SQL string this project sends to a live database passes through here first,
/// mirroring how <c>SilentScan.Verify.Deployment.DdlStatementWhitelist</c> is the code-level
/// backstop for "corpus DML is never executed, anywhere" rather than resting on manifest
/// curation or code review alone. Parses the text with the same ScriptDOM parser the analysis
/// passes use and throws unless every statement in every batch is a bare <see cref="SelectStatement"/> -
/// no DDL, no DML, no EXEC of anything. Live scanning has no legitimate reason to ever need
/// more than that: catalog reads, module bodies, and column diffs are all plain SELECTs.
/// </summary>
public static class LiveReadOnlyGuard
{
    public static void AssertSelectOnly(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("live-query", sql);
        if (parseResult.HasErrors || parseResult.Fragment is not TSqlScript { Batches: var batches })
        {
            throw new InvalidOperationException($"Live query failed to parse as valid T-SQL: {sql}");
        }

        var disallowed = batches.SelectMany(b => b.Statements).FirstOrDefault(s => s is not SelectStatement);
        if (disallowed is not null)
        {
            throw new InvalidOperationException(
                $"Live scanning issues read-only SELECT queries only - refusing to execute a {disallowed.GetType().Name}.");
        }
    }

    /// <summary>
    /// The one path every reader in this project uses to build a command against a live
    /// database - guarding here, once, means no call site can forget to (an <c>AssertSelectOnly</c>
    /// call left out at one of a dozen scattered <c>CreateCommand</c> sites is exactly the kind
    /// of omission code review alone reliably misses).
    /// </summary>
    public static SqlCommand CreateReadOnlyCommand(this SqlConnection connection, string commandText)
    {
        AssertSelectOnly(commandText);
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command;
    }
}
