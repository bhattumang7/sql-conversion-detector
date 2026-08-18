using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum StatementShapeFindingKind
{
    /// <summary>An INSERT with no explicit column list - silently breaks (wrong values in wrong
    /// columns, or a hard error) the moment the target table's own column order or count ever
    /// changes, since the VALUES/SELECT list is matched purely by ordinal position.</summary>
    InsertWithoutColumnList,

    /// <summary>An ORDER BY referencing a SELECT-list position by ordinal number (<c>ORDER BY 2</c>)
    /// instead of a column name - silently sorts by the wrong column the moment the SELECT list's
    /// own column order changes, with no error raised.</summary>
    OrdinalOrderBy,

    /// <summary>A TOP row-limiting clause with no ORDER BY anywhere in the same query - Microsoft's
    /// own documentation states plainly which rows come back is not guaranteed in this shape (see
    /// the finding's own detail text for the citation); a later, semantically-identical plan choice
    /// can silently return a different row set for the same query.</summary>
    TopWithoutOrderBy,

    /// <summary>A base table with no PRIMARY KEY constraint at all - no engine-enforced row
    /// uniqueness, which blocks replication/change-tracking features that require one and is a
    /// common root cause of an accidental duplicate-row bug nothing catches structurally.</summary>
    TableWithNoPrimaryKey,

    /// <summary>A CREATE/ALTER PROCEDURE or CREATE/ALTER TRIGGER body with no <c>SET NOCOUNT ON</c>
    /// anywhere - every DML statement executed sends a client-visible "N rows affected" message,
    /// one extra network round trip's worth of protocol chatter per statement, real cost in a
    /// multi-statement routine and invisible today unless someone profiles for it.</summary>
    MissingSetNocountOn,

    /// <summary>A bare <c>SELECT *</c> anywhere in a query's own outermost projection - distinct
    /// from the already-shipped, narrower "SELECT * inside a view/inline TVF narrowed by a real
    /// consumer" lineage finding: this is the general, any-context case, reported at low confidence
    /// since a one-off ad-hoc SELECT * is frequently a deliberate, harmless choice.</summary>
    BareSelectStar,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Statement-shape advice" - the members that survived direct
/// investigation (see the checklist entry for the ones that did NOT: "existence check over an
/// unfiltered SELECT" was oracle-falsified - the optimizer already treats <c>EXISTS (SELECT *
/// FROM T)</c> and <c>EXISTS (SELECT TOP 1 1 FROM T)</c> identically, confirmed via matching plan
/// XML; "requiring an explicit constraint-check mode" was investigated and found to have no real
/// behavioral consequence beyond the already-shipped WITH NOCHECK/untrusted-constraint finding,
/// since WITH CHECK is already ADD CONSTRAINT's own implicit default, confirmed directly on the
/// oracle; "more than N tables written in a join" is superseded by the already-shipped, lineage-
/// resolved <see cref="PostExpansionJoinWidthFinding"/>; "sp_ prefix"/"schema-prefix" are the same
/// concepts as the already-shipped <see cref="NamingFindingKind.SpPrefixOnUserRoutine"/>/<see
/// cref="NamingFindingKind.UnqualifiedCreate"/>; "UPDATE/DELETE with no WHERE" is superseded by the
/// more precisely-scoped "DBA-script family sweep" entry elsewhere in the checklist).
///
/// <see cref="StatementShapeFindingKind.TableWithNoPrimaryKey"/> is catalog-only (no AST, mirrors
/// <see cref="MaxTypedColumnFinding"/>'s own "one structural fact per table" shape); every other
/// member is a directly observable AST/parse fact - no catalog, no oracle, none of these make a
/// plan-shape or runtime-behavior claim (<see cref="StatementShapeFindingKind.TopWithoutOrderBy"/>'s
/// own "not guaranteed" claim is Microsoft's own documented behavior, cited directly, not an
/// inference this pass makes about a specific plan).
/// </summary>
public sealed record StatementShapeFinding(
    StatementShapeFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

