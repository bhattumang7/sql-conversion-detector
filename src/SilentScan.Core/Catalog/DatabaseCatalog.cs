using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Catalog;

/// <summary>All tables/views/temp tables/table variables discovered across a scanned folder (Pass 1 output).</summary>
public sealed class DatabaseCatalog
{
    private readonly Dictionary<string, CatalogTable> _tablesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType> _typeAliasesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType?> _scalarFunctionReturnTypesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _synonymTargetsByQualifiedName =
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

    public Collation? DefaultCollation { get; set; }

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

    /// <summary>Looks up a real table by its bare qualified name - never scoped.</summary>
    public CatalogTable? Find(string qualifiedName) =>
        _tablesByQualifiedName.GetValueOrDefault(qualifiedName);

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
    /// variables/TVP shapes, or a scalar UDF's return type - those exist only as text inside a
    /// module body, which the live pass DOES parse (for predicate analysis) but never previously
    /// fed through <see cref="CatalogBuilder"/> at all. Merges exactly what a
    /// <see cref="CatalogBuilder"/> pass over those SAME parsed module bodies can contribute that
    /// engine metadata cannot: <see cref="CatalogTableKind.TemporaryTable"/>/
    /// <see cref="CatalogTableKind.TableVariable"/>/<see cref="CatalogTableKind.TableType"/>
    /// entries and scalar-UDF return types. Real <see cref="CatalogTableKind.Table"/> entries
    /// from <paramref name="fileModeCatalog"/> are deliberately never merged - live's own engine-
    /// read tables are authoritative and must never be overwritten by a DDL-text guess (module
    /// bodies contain no CREATE TABLE for a real persistent table anyway, so this filter is a
    /// safety net, not something expected to actually trigger). Type aliases are likewise
    /// skipped - live already reads those straight from <c>sys.types</c>, a stronger source than
    /// re-deriving them from parsed text.
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
    }
}
