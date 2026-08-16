namespace SilentScan.Core.Predicates;

public enum SetOptionFindingKind
{
    /// <summary>The module was compiled under QUOTED_IDENTIFIER OFF (sys.sql_modules.uses_quoted_identifier) - baked in wholesale at CREATE/ALTER time, invisible to a mid-body SET.</summary>
    QuotedIdentifierOffBlocksIndexedFeature,

    /// <summary>An explicit SET NUMERIC_ROUNDABORT ON statement in the module's own body.</summary>
    NumericRoundabortOnBlocksIndexedFeature,

    /// <summary>The module was compiled under ANSI_NULLS OFF (sys.sql_modules.uses_ansi_nulls) - baked in wholesale at CREATE/ALTER time, invisible to a mid-body SET, same shape as QUOTED_IDENTIFIER.</summary>
    AnsiNullsOffBlocksIndexedFeature,

    /// <summary>An explicit SET ANSI_WARNINGS OFF statement in the module's own body.</summary>
    AnsiWarningsOffBlocksIndexedFeature,

    /// <summary>An explicit SET CONCAT_NULL_YIELDS_NULL OFF statement in the module's own body.</summary>
    ConcatNullYieldsNullOffBlocksIndexedFeature,
}

/// <summary>
/// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
/// QUOTED_IDENTIFIER OFF, NUMERIC_ROUNDABORT ON, ANSI_NULLS OFF, ANSI_WARNINGS OFF, and
/// CONCAT_NULL_YIELDS_NULL OFF each independently make an indexed view or filtered index unusable
/// by the optimizer for the query that runs under them, falling back to a base-table/heap scan
/// with no error and no visible predicate change - the plan consequence is invisible at the call
/// site. Oracle-confirmed directly (Docker SQL Server 2022, real seeded data): all five settings
/// demonstrably degrade a filtered-index seek to a table scan.
/// <b>ARITHABORT OFF was investigated and deliberately excluded</b>, contradicting the
/// checklist's own original premise (which lumped all six "mandatory SET options for indexed
/// views/filtered indexes" together) - oracle-probed directly, with real seeded data and a real
/// indexed view AND a real filtered index, ARITHABORT OFF alone changed neither plan at all on
/// this engine version/edition: the filtered index still sought, the indexed view still matched.
/// Publishing it anyway would have been exactly the false positive CLAUDE.md's "precision beats
/// recall everywhere" rule exists to prevent. ANSI_PADDING is a genuinely different, already-
/// shipped, DATA-semantics claim (<see cref="AnsiPaddingMismatchFinding"/>), not a plan-feature
/// one - the two must not be conflated. Only reported when <see
/// cref="Predicates.ModuleReachableObjectWalker"/> proves the module's own body actually touches
/// a filtered-index table or an indexed view (directly, or through a referenced view however
/// many layers down) - the mandatory precision guard: a module that never touches either pays
/// nothing for the SET regardless of its value.
/// <see cref="TouchedObjectQualifiedName"/>/<see cref="TouchedIndexName"/>/<see
/// cref="TouchedIsIndexedView"/> name what the guard actually matched. For <see
/// cref="SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature"/>, <see cref="Line"/>/<see
/// cref="Column"/> point at the module's own CREATE/ALTER statement - there is no SET statement
/// to point at, since the flag is a catalog-level compile setting, not module text.
/// </summary>
public sealed record SetOptionFinding(
    SetOptionFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string? TouchedObjectQualifiedName = null,
    string? TouchedIndexName = null,
    bool TouchedIsIndexedView = false,
    FindingConfidence Confidence = FindingConfidence.High);
