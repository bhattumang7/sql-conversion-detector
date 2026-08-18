using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SelfReferencingDmlFindingKind
{
    /// <summary>The read side names the write target's own table directly - <c>INSERT INTO T SELECT ... FROM T WHERE ...</c>, <c>UPDATE t1 SET ... FROM T t1 JOIN T t2 ON ...</c>, <c>DELETE FROM T WHERE EXISTS (SELECT 1 FROM T ...)</c>, or a MERGE whose USING source (or a WHEN-clause action body) names it.</summary>
    DirectTableReference,

    /// <summary>The read side names a view/inline TVF whose own transitive base-table set (<see cref="Lineage.ViewExpansionOrigin.BaseTables"/>) includes the write target - oracle-confirmed to trigger the identical protective plan behavior as a direct reference, so it is not a lesser or different claim, just a less obvious one to spot by eye.</summary>
    ThroughView,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 "Halloween Protection and self-referencing DML": an
/// <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c> whose own read side (source query,
/// self-join, or a WHERE/SET subquery) also names the exact table it writes to. Pure syntax - no
/// catalog verdict, no lineage type inference - <see cref="Lineage.ViewExpansionMap"/> is used only
/// to resolve <see cref="SelfReferencingDmlFindingKind.ThroughView"/>'s indirection, not for any
/// type/collation question.
///
/// <b>Oracle discovery, load-bearing for how this finding is described - the checklist's own
/// premise ("forces a blocking eager spool") is half right, corrected here:</b> a compile-only
/// <c>SET SHOWPLAN_XML</c> probe against four self-referencing shapes (INSERT hole-filling INSERT
/// ... WHERE NOT EXISTS, DELETE ... WHERE EXISTS, UPDATE ... FROM self-join, MERGE self-reference),
/// each cross-checked against an otherwise-identical control statement reading a DIFFERENT table
/// instead, found TWO distinct protective mechanisms, not one:
/// <list type="bullet">
/// <item>INSERT and DELETE: a genuine <c>PhysicalOp="Table Spool" LogicalOp="Eager Spool"</c>
/// operator is inserted - exactly the checklist's own claim.</item>
/// <item>UPDATE ... FROM self-join and MERGE: NO spool appears at all - instead the plan gains an
/// extra <c>Sort</c> operator (<c>LogicalOp="Distinct Sort"</c> for UPDATE, plain
/// <c>LogicalOp="Sort"</c> for MERGE) that is completely ABSENT from the otherwise-identical
/// cross-table control. This Sort materializes and reorders the join's own output by the target's
/// key before any row is written - the same "read fully before you write" correctness guarantee an
/// Eager Spool provides, just a different physical operator SQL Server chooses to get there.</item>
/// </list>
/// Both mechanisms are absent from every control statement (identical shape, different source
/// table) - so "the read side names the write target" reliably predicts SOME extra defensive plan
/// work across all four statement kinds, even though which specific operator appears depends on
/// the statement's own shape. The finding's own message says "extra defensive plan work (a spool or
/// sort the engine would not otherwise need)" rather than naming one specific operator, so it never
/// overclaims a spool where the real oracle-observed mechanism is a sort. Also oracle-confirmed:
/// reading through a VIEW over the same base table triggers the identical Eager Spool an INSERT
/// case gets from a direct reference - <see cref="SelfReferencingDmlFindingKind.ThroughView"/>
/// exists because of this, not as a guess.
///
/// <b>Known v1 scope limits, stated honestly rather than silently missed:</b> only a
/// <see cref="Microsoft.SqlServer.TransactSql.ScriptDom.NamedTableReference"/> read-side match is
/// covered - an inline-TVF-call-syntax reference to the same table's own MSTVF wrapper, or a
/// synonym chain resolving back to the target from deep inside a nested subquery the visitor
/// doesn't walk, is not chased. A WHERE/SET-clause subquery re-using the outer target's own alias
/// for an UNRELATED table inside its own nested scope (alias shadowing) is not disambiguated from a
/// genuine self-reference sharing that alias - a real but narrow precision risk this scanner accepts
/// per this session's syntax-only-rule discipline, since resolving it soundly needs full nested-
/// scope tracking this pass does not build. A self-join whose two sides are provably disjoint by a
/// static predicate (e.g. <c>t1.Region = 'US' AND t2.Region = 'EU'</c>) still fires - proving
/// disjointness statically is out of scope, matching how <see cref="NonUniqueUpdateSourceFinding"/>
/// accepts the same class of over-reporting for a fan-out risk it cannot statically rule out either.
/// A performance-cost finding, not a correctness one - the result is identical either way, only the
/// plan's own extra defensive cost differs - so <see cref="FindingConfidence.High"/> by default but
/// SARIF Warning, the same "structural risk, not provably-wrong-result" tier
/// <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/> already use.
/// </summary>
public sealed record SelfReferencingDmlFinding(
    SelfReferencingDmlFindingKind Kind,
    string StatementKind,
    string TargetTableQualifiedName,
    string ReadSideQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

