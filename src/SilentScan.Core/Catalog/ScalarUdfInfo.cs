namespace SilentScan.Core.Catalog;

/// <summary>
/// Everything the scalar-UDF stream needs about a function that the return-type registry
/// (<see cref="DatabaseCatalog.AddScalarFunctionReturnType"/>) doesn't already carry - kept as
/// a separate registry rather than folded into that one because the two have independent
/// consumers and independent lifetimes (a return type can resolve from file-mode text alone;
/// several of these fields are live-mode-only truth).
/// </summary>
/// <param name="Kind">T-SQL body vs CLR (<c>EXTERNAL NAME</c>).</param>
/// <param name="IsSchemaBound">
/// <c>WITH SCHEMABINDING</c> presence - null only when this scan never determined it (never
/// guessed false). A non-schemabound function defeats constant-folding of even literal
/// arguments, since the engine can't prove it deterministic.
/// </param>
/// <param name="EngineIsInlineable">
/// <c>sys.sql_modules.is_inlineable</c> - live mode only (2019+ engines), null in file mode and
/// on older engines. When present this is the authoritative inlineability answer; when absent,
/// callers fall back to <see cref="InlineabilityBlocker"/> and must still report Unknown rather
/// than assert inlineable from a clean static scan alone.
/// </param>
/// <param name="InlineabilityBlocker">
/// A human-readable reason a static body scan found the FROID inlineability blocker list
/// (docs/detection-reference.md Appendix 3) tripped - null when the scan found nothing. This is
/// always an EXPLANATION, never the sole basis for asserting <c>NotInlineable</c> confidently
/// over what the engine itself reports; a null value here does NOT mean "inlineable", only that
/// this scan's necessarily-incomplete blocker list found nothing.
/// </param>
/// <param name="ClrDataAccess">
/// True only when the catalog proves a CLR scalar UDF has user or system data access
/// (<c>OBJECTPROPERTYEX(..., 'UserDataAccess'/'SystemDataAccess')</c> in live mode) - null when
/// unknown, never guessed. Unproven data access still reports per-row cost, just without the
/// forces-serial claim.
/// </param>
public sealed record ScalarUdfInfo(
    ScalarUdfKind Kind,
    bool? IsSchemaBound,
    bool? EngineIsInlineable,
    string? InlineabilityBlocker,
    bool? ClrDataAccess);
