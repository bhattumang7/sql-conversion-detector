using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Catalog;

/// <summary>All tables/views/temp tables/table variables discovered across a scanned folder (Pass 1 output).</summary>
public sealed class DatabaseCatalog
{
    private readonly Dictionary<string, CatalogTable> _tablesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SqlType> _typeAliasesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

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

    private static string Key(string qualifiedName, string? scope) =>
        scope is null ? qualifiedName : $"{scope}::{qualifiedName}";
}
