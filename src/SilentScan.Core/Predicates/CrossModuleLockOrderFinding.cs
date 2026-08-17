namespace SilentScan.Core.Predicates;

/// <summary>
/// One procedure's own evidence for a <see cref="CrossModuleLockOrderFinding"/>: the site of the
/// enclosing procedure itself, plus the source line of each of the two write statements that
/// establish the write order this procedure's own explicit transaction takes across the finding's
/// table pair.
/// </summary>
public sealed record LockOrderProcedureSite(
    string ProcedureQualifiedName, string SourcePath, int ProcedureLine, int FirstWriteLine, int SecondWriteLine);

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §D "Cross-module analysis" -
/// "inconsistent lock ordering across modules": two procedures whose own explicit transactions
/// each write the SAME two base tables, in opposite relative order - the textbook deadlock shape
/// (session 1 holds T1's lock and waits for T2, session 2 holds T2's lock and waits for T1, neither
/// can proceed). <see cref="FirstTableQualifiedName"/>/<see cref="SecondTableQualifiedName"/> are
/// ordered canonically (ordinal string comparison) so the same table pair always produces the same
/// finding shape regardless of which procedure happens to be scanned first; <see
/// cref="FirstTableFirstOrdering"/> is whichever procedure's own transaction writes
/// <see cref="FirstTableQualifiedName"/> before <see cref="SecondTableQualifiedName"/>, and <see
/// cref="SecondTableFirstOrdering"/> is the procedure that writes them the other way round.
///
/// V1 scope, deliberately narrower than the full call-graph-reachable version the checklist first
/// sketched ("provable statically from write targets in call-graph order"): this compares only
/// pairs of TOP-LEVEL stored procedures' own DIRECT bodies - not transitively through a nested
/// EXEC call to a third procedure. T-SQL has no way for one procedure's body to be textually
/// nested inside another's, so two top-level procedures are unconditionally "separate entry
/// points a client could call independently" by construction - the full-reachability version's
/// own "nested inside the other's call graph" exclusion is automatically satisfied without
/// needing to walk <see cref="ProcCallGraph"/> at all for this v1. A future v2 could extend this
/// through the call graph (comparing a top-level procedure's own transitive write order, not just
/// its direct one) - explicitly left as a scope-down, not a soundness gap in what IS reported:
/// every pair this v1 reports is two REAL top-level procedures' own real direct write order.
///
/// Further v1 precision guards, all direct AST/catalog facts: only a direct INSERT/UPDATE/
/// DELETE/MERGE target counts as a "write" (never through a view, and never a dynamic-SQL call
/// this pass can't see inside); only a base table (<see cref="Catalog.CatalogTableKind.Table"/>) -
/// never a temp table/table variable, which is private per session and structurally cannot
/// participate in a cross-session deadlock; only writes inside an EXPLICIT transaction (a real
/// <c>BEGIN TRANSACTION</c>...<c>COMMIT</c>/<c>ROLLBACK</c> region the procedure's own body opens
/// and closes) - a write outside any transaction commits individually and releases its locks
/// before the next statement runs, so it cannot hold T1's lock while waiting on T2 the way this
/// deadlock shape requires. <see cref="FindingConfidence.Medium"/>: the write-order fact itself is
/// mechanical and exact, but this is a STATIC deadlock-risk claim, not a runtime guarantee - actual
/// deadlocking additionally needs both procedures' transactions to interleave in the unlucky order
/// at the SAME real moment, real row-level lock granularity (two transactions writing disjoint
/// rows of the same two tables in opposite statement order do not actually conflict), and neither
/// procedure to already hold a lock hierarchy that prevents the interleaving - none of which this
/// pass can see from source text alone.
/// </summary>
public sealed record CrossModuleLockOrderFinding(
    string FirstTableQualifiedName,
    string SecondTableQualifiedName,
    LockOrderProcedureSite FirstTableFirstOrdering,
    LockOrderProcedureSite SecondTableFirstOrdering,
    FindingConfidence Confidence = FindingConfidence.Medium);
