using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Finds a module (procedure/view/function/trigger) that names a table/view/synonym the engine's
/// own binder cannot resolve to a real object right now - live-mode only by construction, since
/// the answer depends on what actually exists in the connected database's catalog, not on
/// anything recoverable from DDL text alone.
///
/// Two passes, not one, because the two candidate sources disagree in a way that matters:
/// <c>sys.sql_expression_dependencies</c> is a fast, single-query catalog VIEW, but it is a
/// snapshot recorded at the referencing module's own CREATE/ALTER time - if the missing object
/// was created afterward, that snapshot is simply stale, not evidence of a real bug (the same
/// cache-vs-live distinction <see cref="LiveLineageParityChecker"/> already draws for column
/// types). <c>sys.dm_sql_referenced_entities</c> gives the engine's live, right-now answer for
/// one module at a time, but batching it across every module in one query is unsafe: confirmed
/// directly against <c>Microsoft.Data.SqlClient</c> that the server's own Msg 2020 advisory
/// ("might not include references to all columns") arrives as a thrown <see cref="SqlException"/>
/// that aborts the reader the moment one module's own unresolvable reference is reached, not as a
/// benign <c>SqlConnection.InfoMessage</c> event - a batched multi-module read would silently lose
/// every row after the first broken module. Reconciling the cheap candidate list against a live
/// call per CANDIDATE (never per module - only the small pre-filtered set) keeps this both fast on
/// a large database and immune to a stale dependency snapshot ever becoming a false positive - see
/// <see cref="IsStillUnresolvedLiveAsync"/> for the second wrinkle this reconciliation call itself
/// has: a view/function throws immediately with zero rows rather than reporting in-band, unlike a
/// procedure.
/// </summary>
public sealed class DanglingObjectReferenceChecker
{
    private readonly string _connectionString;

    public DanglingObjectReferenceChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<DanglingObjectReferenceFinding>> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var candidates = await ReadCandidatesAsync(connection, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var findings = new List<DanglingObjectReferenceFinding>();
        foreach (var candidate in candidates.OrderBy(c => c.ModuleQualifiedName, StringComparer.Ordinal).ThenBy(c => c.ReferencedEntityName, StringComparer.Ordinal))
        {
            if (await IsStillUnresolvedLiveAsync(connection, candidate, cancellationToken))
            {
                findings.Add(new DanglingObjectReferenceFinding(
                    candidate.ModuleQualifiedName, candidate.ModuleTypeDescription, candidate.ReferencedEntityName, candidate.ReferencedSchemaName,
                    SourcePath: candidate.ModuleQualifiedName, Line: 1, Column: 1));
            }
        }

        return findings;
    }

    /// <summary>
    /// One query over the persisted dependency catalog, filtered to the exact "this specific
    /// reference cannot be resolved to a real object ID at all" shape: a caller-dependent
    /// reference (<c>EXEC OtherDb..SomeProc</c>) is deferred to the caller's own context by
    /// design, an ambiguous reference (a name that could be a UDF or a UDT column method) is not
    /// a missing-object claim, and a cross-server/cross-database reference can legitimately carry
    /// no resolvable ID from this database alone - none of those three are this rule's claim.
    /// A trigger's own <c>inserted</c>/<c>deleted</c> pseudo-table is excluded for the same
    /// reason: it is never a real catalog object by design, not a broken reference.
    /// </summary>
    private static async Task<List<Candidate>> ReadCandidatesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id) AS module_name,
                o.type_desc,
                d.referenced_entity_name,
                d.referenced_schema_name
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR')
              AND d.referenced_id IS NULL
              AND d.is_caller_dependent = 0
              AND d.is_ambiguous = 0
              AND d.referenced_server_name IS NULL
              AND d.referenced_database_name IS NULL
              AND d.referenced_class = 1
              AND NOT (o.type = 'TR' AND d.referenced_schema_name IS NULL AND d.referenced_entity_name IN (N'inserted', N'deleted'))
            ORDER BY module_name, d.referenced_entity_name;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var candidates = new List<Candidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new Candidate(
                ModuleQualifiedName: reader.GetString(0),
                ModuleTypeDescription: DescribeModuleType(reader.GetString(1)),
                ReferencedEntityName: reader.GetString(2),
                ReferencedSchemaName: await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3)));
        }

        return candidates;
    }

    /// <summary>
    /// Reconciles one candidate against the engine's OWN live answer right now, for this one
    /// module - the same reference, read fresh, not from the persisted snapshot the candidate
    /// list above came from. The server's Msg 2020 advisory (thrown the instant the module has
    /// ANY unresolvable reference, not specifically the one row being checked) is expected here
    /// whenever the candidate is genuinely still broken, and every row this reader wants has
    /// already been read by the time it arrives - see this class's own doc comment for why that
    /// makes catching and discarding it safe rather than a swallowed real error.
    /// </summary>
    private static async Task<bool> IsStillUnresolvedLiveAsync(SqlConnection connection, Candidate candidate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.referenced_entity_name, r.referenced_schema_name
            FROM sys.dm_sql_referenced_entities(@moduleName, 'OBJECT') r
            WHERE r.referenced_id IS NULL
              AND r.is_caller_dependent = 0
              AND r.is_ambiguous = 0
              AND r.referenced_server_name IS NULL
              AND r.referenced_database_name IS NULL
              AND r.referenced_class = 1;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        command.Parameters.AddWithValue("@moduleName", candidate.ModuleQualifiedName);

        var stillUnresolved = false;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entityName = reader.GetString(0);
                var schemaName = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
                if (string.Equals(entityName, candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(schemaName, candidate.ReferencedSchemaName, StringComparison.OrdinalIgnoreCase))
                {
                    stillUnresolved = true;
                }
            }
        }
        catch (SqlException ex) when (ex.Number == 2020)
        {
            // Expected once this module's own broken reference is reached, after every row it
            // has to give has already been read (sys.dm_sql_referenced_entities reports fully
            // in-band per row before this advisory arrives) - not a real error for this call.
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // A view/inline TVF/scalar function's whole body has to bind before this DMV can
            // describe anything at all (oracle-confirmed: unlike a procedure, it throws Msg 208
            // immediately with ZERO rows first, rather than reporting per-statement dependencies
            // in-band). Msg 208's own text names the missing object, so matching it against this
            // candidate's own referenced name is real confirmation, not a guess - and stays
            // unconfirmed (false) for a message naming a DIFFERENT object than this candidate, so
            // an unrelated bind failure elsewhere in the same module can never wrongly confirm it.
            stillUnresolved = ex.Message.Contains(candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase);
        }
        catch (SqlException ex) when (ex.Number == 207)
        {
            // Same whole-module-must-bind requirement as Msg 208 above, but for a column instead
            // of an object: some unrelated statement in this module references a column the
            // engine can't resolve right now, which blocks the DMV from describing ANY of the
            // module's object references, including the one this call is checking. Msg 207 names
            // a column, never this candidate's (object-shaped) ReferencedEntityName, so the same
            // containment check used for 208 correctly leaves this candidate unconfirmed rather
            // than guessing - the live reconciliation is simply inconclusive for this module.
            stillUnresolved = ex.Message.Contains(candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase);
        }

        return stillUnresolved;
    }

    /// <summary>
    /// <c>sys.objects.type_desc</c>'s own values (<c>SQL_STORED_PROCEDURE</c>) read fine in a
    /// query result but are not the wording this tool's findings use anywhere else - matched to
    /// the plain-English module-type phrasing every other module-level finding already uses.
    /// </summary>
    private static string DescribeModuleType(string typeDesc) => typeDesc switch
    {
        "SQL_STORED_PROCEDURE" => "stored procedure",
        "VIEW" => "view",
        "SQL_SCALAR_FUNCTION" => "scalar function",
        "SQL_INLINE_TABLE_VALUED_FUNCTION" => "inline table-valued function",
        "SQL_TABLE_VALUED_FUNCTION" => "table-valued function",
        "SQL_TRIGGER" => "trigger",
        _ => typeDesc,
    };

    private sealed record Candidate(string ModuleQualifiedName, string ModuleTypeDescription, string ReferencedEntityName, string? ReferencedSchemaName);
}
