namespace SilentScan.Core.Catalog;

/// <summary>
/// Where a resolved column's <see cref="Collation"/> came from - surfaced on every finding that
/// carries a collation-dependent verdict, so the study can separate "confirmed from DDL" from
/// "assumed from the corpus manifest" (docs/audit-remediation-plan.md Phase 1.1).
/// </summary>
public enum CollationSource
{
    /// <summary>An explicit COLLATE clause on the column itself.</summary>
    ColumnExplicit,

    /// <summary>The scanned files contained an explicit CREATE DATABASE/ALTER DATABASE ... COLLATE statement.</summary>
    DatabaseDefaultFromDdl,

    /// <summary>No explicit collation appears anywhere in the scanned DDL; the corpus manifest's declaredCollation hint was used instead.</summary>
    DatabaseDefaultFromManifest,
}

/// <summary>
/// A SQL Server collation name and the family it belongs to. The family determines
/// whether an implicit conversion between varchar and nvarchar forces a scan
/// (SQL_* legacy collations) or permits a dynamic range seek (Windows collations).
/// </summary>
public sealed record Collation(string Name, CollationSource Source = CollationSource.ColumnExplicit)
{
    /// <summary>
    /// SQL_* collations (the legacy Sybase-derived family, e.g. SQL_Latin1_General_CP1_CI_AS)
    /// cannot build GetRangeThroughConvert for a varchar/nvarchar mismatch: the predicate
    /// forces a full scan. Windows collations (e.g. Latin1_General_CI_AS) can.
    /// </summary>
    public bool IsSqlFamily => Name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);

    public bool IsWindowsFamily => !IsSqlFamily;
}
