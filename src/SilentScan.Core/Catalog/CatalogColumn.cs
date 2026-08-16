namespace SilentScan.Core.Catalog;

/// <summary>
/// A column as declared in DDL. <see cref="Type"/> is null when the declared type couldn't be
/// resolved (e.g. a user-defined type) - callers must treat that as UNKNOWN, never guess.
/// <paramref name="IsAnsiPadded"/> is <c>sys.columns.is_ansi_padded</c> (docs/detection-
/// checklist.md Tier 1 "SET options that silently disable plan features": "ANSI_PADDING OFF as a
/// second, independent finding") - captured at CREATE time from the then-CURRENT session's
/// ANSI_PADDING setting, so it is genuinely per-column, not per-table/module: one table can hold
/// both a padded and a non-padded `varchar` column. Defaults to <see langword="true"/> (ANSI_PADDING
/// ON, the standard/recommended setting and this codebase's own DDL-deployment default) for every
/// caller that doesn't know or care about this flag - not "unresolved", a real, deliberate
/// assumption that avoids retroactively making every already-shipped fixture/scanner read as
/// "non-padded" the moment this field was added; file mode (`CatalogBuilder`, which never parses
/// session-state history) is the one caller for whom this default is the ONLY answer it can ever
/// give, live mode always overrides it with the engine's own authoritative flag.
/// </summary>
public sealed record CatalogColumn(
    string Name,
    SqlType? Type,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsPersisted,
    bool IsAnsiPadded = true);
