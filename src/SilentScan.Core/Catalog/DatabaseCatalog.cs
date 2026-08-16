using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Catalog;

/// <summary>One scalar parameter of a CREATE/ALTER PROCEDURE, in declaration order - <paramref name="Type"/> is null when the parameter's own DataType couldn't be resolved, never guessed.</summary>
public sealed record ProcedureParameterInfo(string Name, SqlType? Type, bool IsOutput);

/// <summary>All tables/views/temp tables/table variables discovered across a scanned folder (Pass 1 output).</summary>
public sealed class DatabaseCatalog
{
    private readonly Dictionary<string, CatalogTable> _tablesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType> _typeAliasesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType?> _scalarFunctionReturnTypesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, TableValuedFunctionKind> _tableValuedFunctionKindsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ScalarUdfInfo> _scalarUdfInfoByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SchemaExpressionReference> _schemaExpressions = [];

    private readonly List<ForeignKeyRelationship> _foreignKeys = [];

    private readonly List<CatalogCheckConstraint> _checkConstraints = [];

    private readonly List<TemporalTablePair> _temporalTablePairs = [];

    private readonly Dictionary<string, IReadOnlyList<CatalogIndex>> _indexedViewIndexesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesQuotedIdentifierByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesAnsiNullsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleIsRecompiledByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleUsesDatabaseCollationByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _moduleIsSchemaBoundByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _synonymTargetsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<ProcedureParameterInfo>> _procedureParametersByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SQL Server forbids chaining (a synonym's target can't itself be a synonym), but a corpus can contain a broken/legacy script that does it anyway - bounds the walk so a real or accidental cycle can never loop instead of resolving.</summary>
    private const int MaxSynonymHops = 8;

    public IReadOnlyCollection<CatalogTable> Tables => _tablesByQualifiedName.Values;

    /// <summary>
    /// CREATE TYPE ... FROM aliases discovered across every scanned file, keyed by qualified
    /// name (docs/audit-remediation-plan.md Phase 6.2) - lets a column/variable/CAST target
    /// declared with a user-defined alias resolve through to the real underlying type instead
    /// of staying permanently UNKNOWN.
    /// </summary>
    public IReadOnlyDictionary<string, SqlType> TypeAliases => _typeAliasesByQualifiedName;

    public void AddTypeAlias(string qualifiedName, SqlType underlyingType) =>
        _typeAliasesByQualifiedName[qualifiedName] = underlyingType;

    /// <summary>
    /// A scalar UDF's <c>RETURNS &lt;type&gt;</c>, keyed by the function's own qualified name -
    /// lets a predicate comparing a column against <c>dbo.SomeFunction(...)</c> type the
    /// function side instead of falling to Unknown for lack of any type at all (the single
    /// highest-value gap the construct coverage audit called out). Stored even when the return
    /// type itself couldn't be resolved (null), mirroring how an unresolvable column type is
    /// still recorded as Type=null rather than left absent - "we saw this function and could
    /// not type it" is a different, honest state from "we never saw this function".
    /// </summary>
    public void AddScalarFunctionReturnType(string qualifiedName, SqlType? returnType) =>
        _scalarFunctionReturnTypesByQualifiedName[qualifiedName] = returnType;

    /// <summary>DROP FUNCTION on a scalar UDF - the counterpart to AddScalarFunctionReturnType, so a dropped-and-never-recreated function stops offering a stale return type to any later predicate that happens to reference the same name.</summary>
    public void RemoveScalarFunctionReturnType(string qualifiedName) =>
        _scalarFunctionReturnTypesByQualifiedName.Remove(qualifiedName);

    /// <summary>True only when a CREATE/ALTER FUNCTION with this qualified name was seen with a scalar (non-table) return type - a table-valued function or an unseen name both return false, so a caller can distinguish "not a scalar UDF" from "a scalar UDF whose type didn't resolve".</summary>
    public bool TryGetScalarFunctionReturnType(string qualifiedName, out SqlType? returnType) =>
        _scalarFunctionReturnTypesByQualifiedName.TryGetValue(qualifiedName, out returnType);

    /// <summary>
    /// Records which flavour of table-valued function a qualified name is - the one fact the
    /// MSTVF-as-fence stream cannot get from the call site, since <c>FROM dbo.fn(@x)</c> reads
    /// identically for an inline TVF (harmless, expanded like a view) and a multi-statement one
    /// (an optimization fence with a fabricated cardinality estimate). Absence is meaningful and
    /// must stay absent: a name this scan never saw as a function is NOT reported as anything,
    /// per the "never guess" rule - see <see cref="TryGetTableValuedFunctionKind"/>.
    /// </summary>
    public void AddTableValuedFunctionKind(string qualifiedName, TableValuedFunctionKind kind) =>
        _tableValuedFunctionKindsByQualifiedName[qualifiedName] = kind;

    /// <summary>DROP FUNCTION on a TVF - the counterpart to <see cref="AddTableValuedFunctionKind"/>, so a dropped-and-never-recreated function stops claiming a kind for any later reference that happens to reuse its name.</summary>
    public void RemoveTableValuedFunctionKind(string qualifiedName) =>
        _tableValuedFunctionKindsByQualifiedName.Remove(qualifiedName);

    /// <summary>
    /// True only when a table-valued function with this qualified name was seen (live:
    /// <c>sys.objects.type</c> in <c>('IF','TF','FT')</c>; file mode: its parsed <c>RETURNS</c>
    /// clause). False means "this scan does not know that name to be a TVF" - which covers both a
    /// scalar UDF and a name whose DDL was never read, and must never be treated as "therefore
    /// inline, therefore harmless."
    /// </summary>
    public bool TryGetTableValuedFunctionKind(string qualifiedName, out TableValuedFunctionKind kind) =>
        _tableValuedFunctionKindsByQualifiedName.TryGetValue(qualifiedName, out kind);

    /// <summary>
    /// The scalar-UDF stream's own metadata for a function - T-SQL vs CLR, schemabinding,
    /// engine-reported inlineability, a static blocker-scan explanation, and CLR data access.
    /// Independent of, and merged separately from, <see cref="AddScalarFunctionReturnType"/>
    /// (the return-type registry has its own older consumers this one must not disturb). A live
    /// re-registration (e.g. a later merge overlaying engine-authoritative flags) replaces the
    /// whole record rather than patching individual fields, since <see cref="MergeFileModeExtras"/>
    /// only ever contributes a record for a name live mode never independently populated.
    /// </summary>
    public void AddScalarUdfInfo(string qualifiedName, ScalarUdfInfo info) =>
        _scalarUdfInfoByQualifiedName[qualifiedName] = info;

    /// <summary>DROP FUNCTION on a scalar UDF - the counterpart to <see cref="AddScalarUdfInfo"/>.</summary>
    public void RemoveScalarUdfInfo(string qualifiedName) =>
        _scalarUdfInfoByQualifiedName.Remove(qualifiedName);

    /// <summary>
    /// True only when a scalar UDF with this qualified name was seen. False covers both a
    /// table-valued function and a name this scan never read DDL for - never treated as "not a
    /// UDF at all, therefore safe to ignore" by a caller that already knows it's looking at a
    /// function call, since a genuine miss here just means "this scan doesn't know", not "this
    /// isn't a scalar UDF".
    /// </summary>
    public bool TryGetScalarUdfInfo(string qualifiedName, out ScalarUdfInfo? info) =>
        _scalarUdfInfoByQualifiedName.TryGetValue(qualifiedName, out info);

    /// <summary>
    /// Records one computed column/DEFAULT/CHECK constraint's own definition text, keyed by
    /// nothing but appended to a flat list - unlike every other registry here, this one is never
    /// looked up by name; a single post-catalog pass (<see cref="Predicates.SchemaDependencyScanner"/>)
    /// walks every entry once, after every scalar UDF in the whole catalog is known, exactly
    /// mirroring how live mode reads these definitions as plain text with no earlier phase to
    /// register into.
    /// </summary>
    public void AddSchemaExpression(SchemaExpressionReference reference) => _schemaExpressions.Add(reference);

    public IReadOnlyList<SchemaExpressionReference> SchemaExpressions => _schemaExpressions;

    /// <summary>
    /// Foreign-key column pairs. Per CLAUDE.md's "everything goes via the database" rule, this is
    /// populated ONLY by <c>LiveCatalogReader</c> reading <c>sys.foreign_key_columns</c> live -
    /// file mode (<c>CatalogBuilder</c>) deliberately does not parse <c>FOREIGN KEY</c> DDL, since
    /// replicating the engine's own constraint-resolution semantics (ALTER-added constraints,
    /// multi-batch definitions) is exactly the "reinventing the database-project wheel" CLAUDE.md
    /// warns against. Always empty for a file-mode scan.
    /// </summary>
    public void AddForeignKey(ForeignKeyRelationship relationship) => _foreignKeys.Add(relationship);

    public IReadOnlyList<ForeignKeyRelationship> ForeignKeys => _foreignKeys;

    /// <summary>CHECK constraints, live from <c>sys.check_constraints</c> only - same "engine-authoritative, never parsed from DDL" reasoning as <see cref="ForeignKeys"/>. Always empty for a file-mode scan.</summary>
    public void AddCheckConstraint(CatalogCheckConstraint constraint) => _checkConstraints.Add(constraint);

    public IReadOnlyList<CatalogCheckConstraint> CheckConstraints => _checkConstraints;

    /// <summary>
    /// A system-versioned temporal table's own current-table/history-table pairing, read live from
    /// <c>sys.tables.temporal_type</c>/<c>history_table_id</c> only (docs/detection-checklist.md
    /// "Temporal table history-side index gap") - same "engine-authoritative, never parsed from
    /// DDL" reasoning as <see cref="ForeignKeys"/>/<see cref="CheckConstraints"/>: file mode has no
    /// parsed representation of <c>WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ...))</c> at all
    /// (<c>SystemCatalogViewRegistry</c> only models <c>sys.tables</c>' own columns, never DDL
    /// clause parsing for this feature), so this is always empty for a file-mode scan. Both the
    /// current table AND its history table are otherwise ordinary <see cref="CatalogTable"/> rows
    /// already carrying their own real <see cref="CatalogTable.Indexes"/> (both come back from the
    /// same plain <c>sys.tables</c>/<c>sys.indexes</c> read every other table does - a history table
    /// is not a distinct catalog object kind, just a table with <c>temporal_type = 1</c>) - this
    /// registry supplies only the missing fact those two rows can't carry on their own: which table
    /// is which side of which pair.
    /// </summary>
    public void AddTemporalTablePair(TemporalTablePair pair) => _temporalTablePairs.Add(pair);

    public IReadOnlyList<TemporalTablePair> TemporalTablePairs => _temporalTablePairs;

    /// <summary>
    /// An indexed view's own clustered/nonclustered index shape, keyed by the view's qualified
    /// name - populated ONLY by <c>LiveCatalogReader</c> reading <c>sys.indexes</c> joined
    /// against <c>sys.views</c> (a view is never a <see cref="CatalogTable"/> in this codebase's
    /// model - it is resolved through lineage as a <see cref="Lineage.ResolvedRelation"/> - so an
    /// indexed view needs this narrow side-registry rather than being folded into <see
    /// cref="Tables"/>, the same way <see cref="ForeignKeys"/> is kept as a flat side-registry
    /// rather than attached to a table). Always empty for a file-mode scan: replicating
    /// <c>CREATE INDEX ... ON aView</c> DDL resolution ourselves is exactly the "reinventing the
    /// database-project wheel" CLAUDE.md warns against, and the object being indexed at all is
    /// itself the fact this exists to record.
    /// </summary>
    public void AddIndexedView(string qualifiedName, IReadOnlyList<CatalogIndex> indexes) =>
        _indexedViewIndexesByQualifiedName[qualifiedName] = indexes;

    public bool IsIndexedView(string qualifiedName) => _indexedViewIndexesByQualifiedName.ContainsKey(qualifiedName);

    /// <summary>
    /// A module's own <c>sys.sql_modules.uses_quoted_identifier</c> flag, baked in wholesale at
    /// CREATE/ALTER compile time (a mid-body <c>SET QUOTED_IDENTIFIER</c> statement has no
    /// bearing on this - the catalog flag is the one the engine actually compiled the module
    /// under). Always empty for a file-mode scan - there is no live <c>sys.sql_modules</c> row to
    /// read the flag from; file-mode DDL never states an intended compile-time QUOTED_IDENTIFIER
    /// setting the way a live database's own catalog does.
    /// </summary>
    public void AddModuleUsesQuotedIdentifier(string qualifiedName, bool usesQuotedIdentifier) =>
        _moduleUsesQuotedIdentifierByQualifiedName[qualifiedName] = usesQuotedIdentifier;

    public bool TryGetModuleUsesQuotedIdentifier(string qualifiedName, out bool usesQuotedIdentifier) =>
        _moduleUsesQuotedIdentifierByQualifiedName.TryGetValue(qualifiedName, out usesQuotedIdentifier);

    /// <summary>
    /// A module's own <c>sys.sql_modules.uses_ansi_nulls</c> flag - same shape and same reasoning
    /// as <see cref="AddModuleUsesQuotedIdentifier"/>, baked in wholesale at CREATE/ALTER compile
    /// time. Oracle-confirmed directly (docs/detection-checklist.md Tier 1 "SET options that
    /// silently disable plan features"): ANSI_NULLS OFF, like QUOTED_IDENTIFIER OFF, makes a
    /// filtered index/indexed view the module touches unusable by the optimizer.
    /// </summary>
    public void AddModuleUsesAnsiNulls(string qualifiedName, bool usesAnsiNulls) =>
        _moduleUsesAnsiNullsByQualifiedName[qualifiedName] = usesAnsiNulls;

    public bool TryGetModuleUsesAnsiNulls(string qualifiedName, out bool usesAnsiNulls) =>
        _moduleUsesAnsiNullsByQualifiedName.TryGetValue(qualifiedName, out usesAnsiNulls);

    /// <summary>
    /// A module's own <c>sys.sql_modules.is_recompiled</c> flag - true only for a routine authored
    /// with <c>WITH RECOMPILE</c> (docs/detection-checklist.md "Small precise adds"). Every
    /// execution compiles a fresh plan and discards it rather than caching it, invisible to any
    /// monitoring that reads the plan cache (<c>sys.dm_exec_cached_plans</c>/<c>sys.dm_exec_query_stats</c>)
    /// - the module's own cost never accumulates there at all. Baked in at CREATE/ALTER time, same
    /// shape as <see cref="AddModuleUsesQuotedIdentifier"/>. Always empty for a file-mode scan -
    /// there is no live <c>sys.sql_modules</c> row to read the flag from.
    /// </summary>
    public void AddModuleIsRecompiled(string qualifiedName, bool isRecompiled) =>
        _moduleIsRecompiledByQualifiedName[qualifiedName] = isRecompiled;

    public bool TryGetModuleIsRecompiled(string qualifiedName, out bool isRecompiled) =>
        _moduleIsRecompiledByQualifiedName.TryGetValue(qualifiedName, out isRecompiled);

    /// <summary>
    /// A module's own <c>sys.sql_modules.uses_database_collation</c> flag - true for a
    /// schema-bound module (an indexed view, a schema-bound function, or a check
    /// constraint/computed column expression) whose own compiled plan resolved a string
    /// comparison or sort by implicitly falling back to the CURRENT database's default collation,
    /// with no explicit <c>COLLATE</c> clause pinning it (docs/detection-checklist.md "Small
    /// precise adds"). Baked in at CREATE/ALTER time; a later <c>ALTER DATABASE ... COLLATE</c>
    /// changes what the module actually compares against without the module's own text ever
    /// changing - oracle-confirmed directly (Docker instance): SQL Server accepts a schema-bound
    /// object with an implicit database-collation dependency, and <c>ALTER DATABASE</c> is not
    /// blocked by its existence, so the object's real string-comparison behavior silently follows
    /// the database's collation wherever it is moved to next. Always empty for a file-mode scan -
    /// there is no live <c>sys.sql_modules</c> row to read the flag from.
    /// </summary>
    public void AddModuleUsesDatabaseCollation(string qualifiedName, bool usesDatabaseCollation) =>
        _moduleUsesDatabaseCollationByQualifiedName[qualifiedName] = usesDatabaseCollation;

    public bool TryGetModuleUsesDatabaseCollation(string qualifiedName, out bool usesDatabaseCollation) =>
        _moduleUsesDatabaseCollationByQualifiedName.TryGetValue(qualifiedName, out usesDatabaseCollation);

    /// <summary>
    /// A module's own <c>sys.sql_modules.is_schema_bound</c> flag, for ANY module kind (view,
    /// function, trigger) - not to be confused with <see cref="ScalarUdfInfo.IsSchemaBound"/>,
    /// which only ever exists for a scalar UDF. Read purely so <see
    /// cref="Predicates.ModuleCompileFlagScanner"/> can EXCLUDE a schema-bound module from its own
    /// <c>uses_database_collation</c> finding (oracle-confirmed: schema-binding sets that flag
    /// unconditionally, regardless of whether the module touches string data at all, so it carries
    /// no differentiating signal there - see <see cref="Predicates.ModuleCompileFlagFinding"/>'s
    /// own doc comment). Always empty for a file-mode scan.
    /// </summary>
    public void AddModuleIsSchemaBound(string qualifiedName, bool isSchemaBound) =>
        _moduleIsSchemaBoundByQualifiedName[qualifiedName] = isSchemaBound;

    public bool TryGetModuleIsSchemaBound(string qualifiedName, out bool isSchemaBound) =>
        _moduleIsSchemaBoundByQualifiedName.TryGetValue(qualifiedName, out isSchemaBound);

    /// <summary>
    /// A CREATE/ALTER PROCEDURE's own declared parameter list, in declaration order, keyed by the
    /// procedure's qualified name - the foundation the procedure call graph (<see
    /// cref="Predicates.ProcCallGraphBuilder"/>) matches an <c>EXEC</c> call site's positional and
    /// named arguments against. Registered even when a parameter's type couldn't be resolved
    /// (null), matching the same "we saw this and could not type it" honesty
    /// <see cref="AddScalarFunctionReturnType"/> already follows for functions. A table-valued
    /// parameter is deliberately excluded here - it has no scalar SqlType to seed anything with,
    /// and is already registered separately as a scoped table (<see cref="AddOrReplace(CatalogTable, string?)"/>).
    /// </summary>
    /// <summary>
    /// A parameterless registration never overwrites an ALREADY-registered non-empty one for the
    /// same qualified name - real corpus shape (<see cref="DynamicSqlTempTableDiscovery"/>): its
    /// own synthetic <c>CREATE PROCEDURE [schema].[name] AS BEGIN ... END</c> wrapper, reusing a
    /// REAL procedure's own qualified name/scope so a dynamic-SQL-constructed temp table resolves
    /// under the right scope, is built with NO parameter list at all (it only ever wraps a folded
    /// CREATE TABLE snippet) and runs through this same CatalogBuilder pass a second time, AFTER
    /// the real procedure's own full body already registered its true parameter list correctly -
    /// without this guard, that second, parameter-less pass silently clobbers every later EXEC
    /// call-graph edge's own argument-to-formal matching down to zero formals, discarding the
    /// procedure's real parameters entirely (surfaced as widespread false "variable-not-in-scope"
    /// findings for genuine, correctly-declared parameters). A GENUINE parameterless procedure
    /// registering 0 params for the first time, or re-registering the SAME 0, is unaffected -
    /// this only ever blocks a 0-length list from replacing a already-known non-0-length one.
    /// </summary>
    public void AddProcedureParameters(string qualifiedName, IReadOnlyList<ProcedureParameterInfo> parameters)
    {
        if (parameters.Count == 0 && _procedureParametersByQualifiedName.TryGetValue(qualifiedName, out var existing) && existing.Count > 0)
        {
            return;
        }

        _procedureParametersByQualifiedName[qualifiedName] = parameters;
    }

    /// <summary>True only when a CREATE/ALTER PROCEDURE with this qualified name was seen - an unregistered/unresolvable callee (a system proc, or a name this scan never saw) returns false rather than an empty list, so a caller can tell "no parameters" apart from "unknown procedure".</summary>
    public bool TryGetProcedureParameters(string qualifiedName, out IReadOnlyList<ProcedureParameterInfo> parameters) =>
        _procedureParametersByQualifiedName.TryGetValue(qualifiedName, out parameters!);

    /// <summary>Registers <c>CREATE SYNONYM name FOR target</c> - a pure name-&gt;name mapping, so it belongs in the same phase type aliases do (nothing else needs to have been resolved first).</summary>
    public void AddSynonym(string qualifiedName, string targetQualifiedName) =>
        _synonymTargetsByQualifiedName[qualifiedName] = targetQualifiedName;

    /// <summary><c>DROP SYNONYM</c> - matches CatalogBuilder's single-phase, file-order-is-declaration-order treatment of every other name-only mapping.</summary>
    public void RemoveSynonym(string qualifiedName) =>
        _synonymTargetsByQualifiedName.Remove(qualifiedName);

    /// <summary>
    /// Walks a chain of synonyms to the real name a FROM-clause reference ultimately means -
    /// <paramref name="qualifiedName"/> unchanged if it isn't a synonym at all. Real SQL Server
    /// never chains synonyms, but this pass doesn't reject the DDL that tries to; a cycle or a
    /// chain longer than <see cref="MaxSynonymHops"/> returns the ORIGINAL input rather than a
    /// partially-walked name, so the caller's ordinary "no known DDL" path reports it honestly
    /// instead of resolving to a guess.
    /// </summary>
    public string ResolveSynonymName(string qualifiedName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = qualifiedName;

        while (_synonymTargetsByQualifiedName.TryGetValue(current, out var next))
        {
            if (!seen.Add(current) || seen.Count > MaxSynonymHops)
            {
                return qualifiedName;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// The name of the database this catalog was actually built against - set by
    /// <c>LiveCatalogReader</c> from the live connection's own <c>SqlConnection.Database</c> for
    /// a <c>scan-db</c> run; left null for a file-mode/corpus scan, where there is no single
    /// "current database" the parsed DDL was deployed under in the same sense. Used only to
    /// recognize a THREE-PART reference that names THIS SAME database by its own full name
    /// (<see cref="Find(string)"/>) - never to resolve a reference to a genuinely different
    /// database, which stays correctly unresolvable (no second connection, per CLAUDE.md hard
    /// scope).
    /// </summary>
    public string? CurrentDatabaseName { get; set; }

    public Collation? DefaultCollation { get; set; }

    /// <summary>
    /// tempdb's own server-level collation - a real SQL Server's tempdb frequently differs from
    /// a user database's collation (it's set once at instance install time, not per-database), so
    /// a #temp table or table variable's columns should default to THIS, not
    /// <see cref="DefaultCollation"/>. Null (the default) falls back to <see cref="DefaultCollation"/>
    /// exactly like before this property existed - set only when a manifest/CLI value actually
    /// supplies one, never guessed.
    /// </summary>
    public Collation? TempdbCollation { get; set; }

    /// <summary>The collation a temp table/table variable's columns should default to - <see cref="TempdbCollation"/> when known, else <see cref="DefaultCollation"/> (today's behavior, preserved when tempdb's own collation was never supplied).</summary>
    public Collation? EffectiveTempdbCollation => TempdbCollation ?? DefaultCollation;

    /// <summary>Everything Pass 1 saw but could not resolve into catalog data - never silently dropped.</summary>
    public SkipLedger Skipped { get; } = new();

    /// <summary>
    /// Stores a real table under its bare qualified name. A temp table or table variable
    /// declared inside a procedure/function body should use
    /// <see cref="AddOrReplace(CatalogTable, string?)"/> with that procedure's name as the
    /// scope, so two procedures' same-named-but-differently-shaped temp objects (a very common
    /// real-world pattern) don't clobber each other (docs/audit-remediation-plan.md Phase 2.5).
    /// </summary>
    public void AddOrReplace(CatalogTable table) => AddOrReplace(table, scope: null);

    /// <summary>
    /// Stores <paramref name="table"/> under a key scoped to <paramref name="scope"/> (typically
    /// the qualified name of the enclosing procedure/function/trigger a temp table or table
    /// variable was declared in) when <paramref name="scope"/> is non-null; otherwise behaves
    /// like the unscoped overload. Real persistent tables are never scoped - only
    /// <see cref="CatalogTableKind.TemporaryTable"/>/<see cref="CatalogTableKind.TableVariable"/>
    /// objects are, since only those can legitimately collide by name across procedures.
    /// </summary>
    public void AddOrReplace(CatalogTable table, string? scope) =>
        _tablesByQualifiedName[Key(table.QualifiedName, scope)] = table;

    /// <summary>
    /// Looks up a real table by its bare qualified name - never scoped. When the direct lookup
    /// misses and <paramref name="qualifiedName"/> carries a three-part database prefix that
    /// case-insensitively matches <see cref="CurrentDatabaseName"/> (e.g. real corpus code
    /// self-referencing its own database by full name - <c>SchemaObjectNameHelper.Qualify</c>
    /// always keeps a database qualifier distinct from a bare name, since db.dbo.T and dbo.T
    /// must never collapse into the same catalog key for a GENUINELY different database), retries
    /// with that prefix stripped - the underlying table IS this same catalog's own entry, just
    /// named more explicitly than usual. A prefix that does NOT match stays unresolved: this is
    /// deliberately never a heuristic "assume it's probably us" - only an exact, known match to
    /// the database this catalog was actually built against ever gets stripped.
    /// </summary>
    public CatalogTable? Find(string qualifiedName)
    {
        if (_tablesByQualifiedName.TryGetValue(qualifiedName, out var table))
        {
            return table;
        }

        return TryStripSelfReferencingDatabasePrefix(qualifiedName) is { } normalized
            ? _tablesByQualifiedName.GetValueOrDefault(normalized)
            : null;
    }

    /// <summary>
    /// Looks up a temp table/table variable, trying <paramref name="scope"/>-qualified first
    /// (the common case: referenced from within the same procedure that declared it) and
    /// falling back to the batch-level unscoped entry (a temp object declared and used outside
    /// any procedure, or - conservatively - one this pass couldn't determine a scope for).
    /// </summary>
    public CatalogTable? Find(string qualifiedName, string? scope)
    {
        if (scope is not null && _tablesByQualifiedName.TryGetValue(Key(qualifiedName, scope), out var scoped))
        {
            return scoped;
        }

        return Find(qualifiedName);
    }

    private string? TryStripSelfReferencingDatabasePrefix(string qualifiedName)
    {
        if (CurrentDatabaseName is not { Length: > 0 } currentDatabaseName)
        {
            return null;
        }

        var prefix = currentDatabaseName + ".";
        return qualifiedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? qualifiedName[prefix.Length..] : null;
    }

    /// <summary>
    /// DROP TABLE/VIEW-as-table/etc. and the "remove the old key" half of sp_rename - removes
    /// whichever entry <paramref name="scope"/>-qualified lookup would have found (falling back
    /// to the unscoped key, matching <see cref="Find(string, string?)"/>'s own fallback), so a
    /// dropped-and-never-recreated object stops offering a stale definition to any later
    /// predicate that references its name (docs/audit-remediation-plan.md Phase 2.5 successor:
    /// catalog lifecycle). A target this pass never cataloged in the first place is a silent
    /// no-op here - the caller is responsible for ledgering that case if it cares (the same
    /// division of responsibility RemoveSynonym and RemoveScalarFunctionReturnType already use).
    /// </summary>
    public void Remove(string qualifiedName, string? scope)
    {
        if (scope is not null)
        {
            _tablesByQualifiedName.Remove(Key(qualifiedName, scope));
        }

        _tablesByQualifiedName.Remove(qualifiedName);
    }

    private static string Key(string qualifiedName, string? scope) =>
        scope is null ? qualifiedName : $"{scope}::{qualifiedName}";

    /// <summary>
    /// Roadmap Phase C2 (live catalog parity): live-mode's catalog comes straight from engine
    /// metadata (<c>LiveCatalogReader</c>), which knows nothing about temp tables/table
    /// variables/TVP shapes, a scalar UDF's return type, or a procedure's own declared parameter
    /// list - those exist only as text inside a module body, which the live pass DOES parse (for
    /// predicate analysis) but never previously fed through <see cref="CatalogBuilder"/> at all.
    /// Merges exactly what a <see cref="CatalogBuilder"/> pass over those SAME parsed module
    /// bodies can contribute that engine metadata cannot: <see cref="CatalogTableKind.TemporaryTable"/>/
    /// <see cref="CatalogTableKind.TableVariable"/>/<see cref="CatalogTableKind.TableType"/>
    /// entries, scalar-UDF return types, scalar-UDF metadata (schemabinding/inlineability/CLR
    /// data access, field-merged rather than replaced - see the loop below), and procedure
    /// parameter lists (the roadmap "trace
    /// provably-constant dynamic SQL across proc-call edges" item's own
    /// <see cref="Predicates.ProcCallGraphBuilder"/> depends entirely on
    /// <see cref="TryGetProcedureParameters"/> to match an EXEC call site's arguments against a
    /// callee's formal parameters - without this merge, every engine-authoritative scan silently
    /// had zero call-graph edges, since nothing ever populated this dictionary for it at all).
    /// Real <see cref="CatalogTableKind.Table"/> entries from <paramref name="fileModeCatalog"/>
    /// are deliberately never merged - live's own engine-read tables are authoritative and must
    /// never be overwritten by a DDL-text guess (module bodies contain no CREATE TABLE for a real
    /// persistent table anyway, so this filter is a safety net, not something expected to
    /// actually trigger). Type aliases are likewise skipped - live already reads those straight
    /// from <c>sys.types</c>, a stronger source than re-deriving them from parsed text.
    /// </summary>
    public void MergeFileModeExtras(DatabaseCatalog fileModeCatalog)
    {
        foreach (var (key, table) in fileModeCatalog._tablesByQualifiedName)
        {
            if (table.Kind is CatalogTableKind.TemporaryTable or CatalogTableKind.TableVariable or CatalogTableKind.TableType)
            {
                _tablesByQualifiedName[key] = table;
            }
        }

        foreach (var (qualifiedName, returnType) in fileModeCatalog._scalarFunctionReturnTypesByQualifiedName)
        {
            _scalarFunctionReturnTypesByQualifiedName[qualifiedName] = returnType;
        }

        foreach (var (qualifiedName, parameters) in fileModeCatalog._procedureParametersByQualifiedName)
        {
            AddProcedureParameters(qualifiedName, parameters);
        }

        // TryAdd, not assignment: unlike the entries above, live mode DOES read TVF kinds
        // straight from sys.objects, and that answer is authoritative. A parsed RETURNS clause
        // only ever fills a name the engine never reported (a module body's own text in a
        // file-mode-only scan), and must never overwrite the engine's own classification.
        foreach (var (qualifiedName, kind) in fileModeCatalog._tableValuedFunctionKindsByQualifiedName)
        {
            _tableValuedFunctionKindsByQualifiedName.TryAdd(qualifiedName, kind);
        }

        // Unlike the TVF-kind merge above, this is a field-level merge, not a whole-record
        // TryAdd: by the time this runs, `this` (the live catalog) may ALREADY hold an entry for
        // the same name, registered straight from sys.sql_modules/OBJECTPROPERTYEX (engine-only
        // fields: IsSchemaBound, EngineIsInlineable, ClrDataAccess) - but that live reader never
        // parses a body, so it never populates InlineabilityBlocker. fileModeCatalog is exactly
        // the reparse of that same module's text through CatalogBuilder (LiveModuleReader feeds
        // FN bodies through it like any other module) and is the ONLY source of the blocker scan.
        // So: keep every engine-sourced field from the live entry (it is strictly stronger truth
        // than anything a text reparse could derive), but backfill Kind/InlineabilityBlocker from
        // the file-mode entry when the live side never set them. A name live mode never
        // independently touched is added as-is.
        foreach (var (qualifiedName, fileInfo) in fileModeCatalog._scalarUdfInfoByQualifiedName)
        {
            _scalarUdfInfoByQualifiedName[qualifiedName] = _scalarUdfInfoByQualifiedName.TryGetValue(qualifiedName, out var liveInfo)
                ? liveInfo with { InlineabilityBlocker = liveInfo.InlineabilityBlocker ?? fileInfo.InlineabilityBlocker }
                : fileInfo;
        }
    }
}
