namespace SilentScan.Core.Predicates;

public enum NamingFindingKind
{
    /// <summary>A declared identifier (table/column/procedure/function/view/variable/parameter/
    /// index/trigger/etc name) is spelled identically to a T-SQL reserved keyword, forcing every
    /// future reference to remember to bracket- or quote-delimit it.</summary>
    ReservedKeywordAsIdentifier,

    /// <summary>A user-defined procedure or function is named with the "sp_" prefix - reserved by
    /// convention for system-shipped procedures. SQL Server searches the master database first for
    /// any unqualified "sp_"-prefixed call, adding lookup overhead and risking a silent collision
    /// with a real (or future) system procedure of the same name.</summary>
    SpPrefixOnUserRoutine,

    /// <summary>A CREATE/ALTER for a schema-scoped object (procedure/function/view/trigger) names it
    /// with no explicit schema qualifier - the object's real owning schema then depends on the
    /// connecting principal's own default schema at deployment time, a real environment-dependent
    /// risk with no fixed, provable answer from the script text alone.</summary>
    UnqualifiedCreate,

    /// <summary>A data type reference inside a column/variable/parameter/CAST declaration carries a
    /// redundant schema (or database) qualifier that adds nothing - built-in and user-defined types
    /// alike are resolved the same way with or without it, so the qualifier is pure noise that
    /// couples the declaration to a schema it does not actually need to name.</summary>
    RedundantTypeQualifier,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Naming and identifiers" plus the related standalone
/// "redundant database/schema qualifier" bullet - four structural naming/identifier checks over the
/// AST, no catalog, no oracle (none of these make a plan-shape or runtime-behavior claim - the same
/// reasoning <see cref="CodeMetricFinding"/>/<see cref="FormattingFinding"/> already established).
///
/// Deliberately does NOT ship a configurable "does this variable/parameter/routine name match some
/// naming-convention pattern" rule with an opinionated default the way the checklist's own "routine
/// name patterns, variable name patterns" phrasing suggested - cross-checked against the real source
/// this Tier 4 entry is derived from and found the variable/parameter naming-pattern rule there ships
/// with a functionally permissive default (matches virtually any valid identifier), while the
/// routine-naming rule's own real default narrows to one specific, well-documented case: the "sp_"
/// prefix anti-pattern, which this codebase ships as its own precise, non-configurable-pattern
/// finding (<see cref="NamingFindingKind.SpPrefixOnUserRoutine"/>) rather than as a generic
/// naming-convention rule with no real default to justify shipping opinionated.
/// </summary>
public sealed record NamingFinding(
    NamingFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium);
