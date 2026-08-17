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
///
/// <paramref name="IdentitySeed"/>/<paramref name="IdentityIncrement"/>/<paramref name="IdentityCurrentValue"/>
/// (docs/detection-checklist.md "DBA-script family sweep" §A "Identity/sequence range exhaustion")
/// are <c>sys.identity_columns.seed_value</c>/<c>increment_value</c>/<c>last_value</c> - read
/// LIVE-ONLY, straight from the same <c>sys.columns</c>-keyed query that reads every other column
/// fact (a single extra LEFT JOIN, no separate round trip), and only ever non-null for a column
/// with <see cref="IsIdentity"/> true. All three are <c>sql_variant</c> at the engine level (the
/// same underlying numeric type the identity column itself declares - int, bigint, decimal(p,0),
/// etc.) and are converted to <see langword="decimal"/> here for uniform arithmetic regardless of
/// which exact identity type produced them.
/// <paramref name="IdentityCurrentValue"/> is a DATA-STATE fact, not a schema fact (docs/
/// detection-checklist.md's own three-way design-time-decidability split): it changes every time a
/// row is inserted, is <see langword="null"/> when the column has never had a row inserted since
/// the table (or the whole database) was created, and is only meaningful for range-exhaustion
/// reasoning against a production-shaped target - never treated as a passing/clean signal on a
/// low-value development database (<c>IdentityRangeScanner</c>'s own doc comment enforces this).
/// <paramref name="IdentitySeed"/>/<paramref name="IdentityIncrement"/> are ordinary schema facts,
/// decidable identically on a dev or production copy of the same schema.
/// </summary>
public sealed record CatalogColumn(
    string Name,
    SqlType? Type,
    bool IsNullable,
    bool IsIdentity,
    bool IsComputed,
    bool IsPersisted,
    bool IsAnsiPadded = true,
    decimal? IdentitySeed = null,
    decimal? IdentityIncrement = null,
    decimal? IdentityCurrentValue = null);
