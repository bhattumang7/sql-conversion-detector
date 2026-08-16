using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Verify.Catalog;

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
    /// <summary>
    /// Default <see cref="SqlCommand.CommandTimeout"/> applied to every live read-only command.
    /// </summary>
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

    /// <summary>
    /// A narrower, separate carve-out - used ONLY for text about to be bound as
    /// <c>sys.dm_exec_describe_first_result_set</c>'s own parameter (see
    /// <c>SilentScan.Live.Catalog.LiveDescribedColumnReader.DescribeProcedureAsync</c>), never
    /// for the outer command text itself, which still goes through
    /// <see cref="CreateReadOnlyCommand"/>/<see cref="AssertSelectOnly"/> like every other live
    /// query. The DMV parses, binds and compiles the batch it's handed and returns result-set
    /// metadata WITHOUT executing it - empirically confirmed compile-only for both a bare
    /// <c>SELECT</c> and an <c>EXEC dbo.SomeProc</c> form against the standing Docker oracle (zero
    /// rows touched in either case) - so accepting a bare named-procedure EXEC here, in addition
    /// to a bare SELECT, extends the same no-execution guarantee <see cref="AssertSelectOnly"/>
    /// gives every other live query, rather than loosening it. A string-form EXEC
    /// (<see cref="ExecutableStringList"/>, e.g. <c>EXEC('...')</c> or <c>EXEC(@sql)</c>) is
    /// still rejected here exactly as it is everywhere else - it could contain arbitrary text,
    /// not a fixed, catalog-known procedure name, so it carries none of this carve-out's
    /// justification.
    /// </summary>
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

    /// <summary>
    /// The one path every reader in this project uses to build a command against a live
    /// database - guarding here, once, means no call site can forget to (an <c>AssertSelectOnly</c>
    /// call left out at one of a dozen scattered <c>CreateCommand</c> sites is exactly the kind
    /// of omission code review alone reliably misses).
    /// </summary>
    /// <param name="connection">The open connection to build the command against.</param>
    /// <param name="commandText">The SELECT-only command text; anything else is rejected by <c>AssertSelectOnly</c>.</param>
    /// <param name="commandTimeoutSeconds">
    /// Wall-clock ceiling for the command, defaulting to <see cref="DefaultCommandTimeoutSeconds"/>.
    /// Catalog and module reads against a large database can outrun ADO.NET's 30-second default,
    /// and a read-only SELECT that runs long is better waited on than aborted mid-scan.
    /// </param>
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
