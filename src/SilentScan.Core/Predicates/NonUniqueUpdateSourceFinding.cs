namespace SilentScan.Core.Predicates;

/// <summary>
/// <c>UPDATE t SET t.Col = s.Col FROM dbo.Target t JOIN dbo.Source s ON ...</c> where the join
/// gives the engine no schema-level guarantee that at most one row of <c>s</c> can match a given
/// row of <c>t</c> (docs/detection-checklist.md Tier 2 "UPDATE ... FROM without source
/// uniqueness"). SQL Server's own documented behavior when a target row matches more than one
/// source row is to silently pick a value from ONE of the matching rows - WHICH one is
/// unspecified, plan-dependent, and not guaranteed stable even across repeated executions of the
/// identical statement. <c>MERGE</c>, by contrast, genuinely raises an error in this exact
/// situation ("The MERGE statement attempted to UPDATE or DELETE the same row more than once...")
/// rather than picking silently - confirmed directly against the standing Docker oracle, not
/// assumed from documentation
/// (<c>NonUniqueUpdateSourceOracleTests</c>).
///
/// <b>A structurally unsafe finding, not a "wrong for current data" one - a meaningfully
/// stronger claim than that distinction usually implies.</b> Unlike a nullable-column trap that
/// needs today's data to already contain a NULL, this defect requires no data inspection at all:
/// the statement has no schema guarantee against a future duplicate join-key value, so it can
/// start returning a different, silently wrong answer the moment a single <c>INSERT</c> happens
/// on <c>s</c> - zero code or schema change required on this statement itself. The absence of the
/// uniqueness guarantee is itself the full, provable defect, the same framing
/// <c>PartialCompositeForeignKeyJoinFinding</c> uses for its own row-multiplication claim.
///
/// <see cref="SourceTableQualifiedName"/>/<see cref="JoinColumnNames"/> is the join found NOT
/// covered by any single unique index/constraint whose key columns are all among the join's own
/// equality columns on that side - a unique constraint over a strict SUPERSET of the join columns
/// does NOT suppress this finding (e.g. a composite <c>UNIQUE(TargetId, Cat)</c> does not make a
/// join on <c>TargetId</c> alone safe), confirmed directly against the oracle. Only fires when
/// the <c>SET</c> clause actually reads a value from the unsafe source (<see
/// cref="SetColumnNames"/>) - a join to a non-unique source used only for filtering carries no
/// risk this rule can see.
///
/// Deliberately narrow, base-table-only, direct-join-only (matching
/// <c>PartialCompositeForeignKeyJoinScanner</c>'s own restraint): only a join where one side is
/// unambiguously the UPDATE's own target (by alias) and the other side is a real base table is
/// examined - a join two hops away from the target, a derived-table/aggregated source (which can
/// be provably unique per-group without any catalog constraint, e.g. a <c>GROUP BY</c> subquery),
/// and a view/CTE-derived source are left unanalyzed rather than guessed at, known v1 scope
/// limits. <c>MERGE</c>'s own <c>USING</c> source is out of scope by construction - the engine
/// itself raises the error there, so there is nothing silent to detect.
///
/// Version-insensitive in the sense that matters here: no compat level or CE mode makes this
/// defined behavior. Which specific row wins on a given execution is plan-dependent and
/// explicitly not a claim this finding makes.
/// </summary>
public sealed record NonUniqueUpdateSourceFinding(
    string TargetTableQualifiedName,
    string SourceTableQualifiedName,
    IReadOnlyList<string> JoinColumnNames,
    IReadOnlyList<string> SetColumnNames,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
