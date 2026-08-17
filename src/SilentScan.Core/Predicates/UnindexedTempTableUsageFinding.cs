namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": a <c>SELECT ... INTO #temp FROM
/// ...</c> temp table later used as a JOIN operand or filtered by a WHERE predicate in the same
/// batch/procedure scope, with no index ever created on it. Reuses the catalog's own existing
/// temp-table tracking (a <c>SELECT INTO</c>'s inferred columns, and any later <c>CREATE INDEX</c>
/// against the same scoped name, are already recorded on the same <see cref="Catalog.CatalogTable"/>
/// entry by <c>CatalogBuilder</c> - no new catalog plumbing needed, only a new AST pass over
/// usage sites).
///
/// Oracle-confirmed the underlying cost claim directly (Docker instance, a 5,000-row seeded
/// source table): a <c>#temp</c> table with no index, joined to a real table on an unindexed
/// column, produces a <c>PhysicalOp="Hash Match"</c> plan reading the ENTIRE temp table (no
/// <c>Index Seek</c>/<c>Index Scan</c> alternative exists at all without an index) - the same
/// cost story as an unindexed base table used the same way, confirmed to hold even at small
/// (sub-1000-row) temp-table sizes where SQL Server's own automatic temp-table statistics do NOT
/// change the fundamental access-path story (no index means no seek is even POSSIBLE, independent
/// of how good the cardinality estimate is).
///
/// Not verdict-bearing (no per-finding plan-XML oracle - the underlying mechanism was confirmed
/// once, matching how the case-fold Tier-1 rule confirms its own general mechanism once rather
/// than per finding). <see cref="FindingConfidence.Medium"/> (this pass
/// cannot see the temp table's real row count - a genuinely tiny temp table, even unindexed, may
/// never matter in practice, the same honesty <see cref="PartialCompositeForeignKeyJoinFinding"/>
/// already applies for its own data-dependent fan-out risk), SARIF Warning.
/// </summary>
public enum UnindexedTempTableUsageKind
{
    JoinOperand,
    FilteredInWhere,
}

public sealed record UnindexedTempTableUsageFinding(
    UnindexedTempTableUsageKind Kind,
    string TempTableQualifiedName,
    string SourcePath,
    int DeclarationLine,
    int UsageLine,
    int UsageColumn,
    FindingConfidence Confidence = FindingConfidence.Medium);
