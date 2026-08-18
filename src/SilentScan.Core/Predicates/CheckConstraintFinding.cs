using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum CheckConstraintFindingKind
{
    /// <summary>
    /// docs/detection-checklist.md Tier 2 §A "CHECK constraint that doesn't account for NULL"
    /// (source: Brent Ozar's "Keep it Constrained") - a nullable column's own CHECK constraint
    /// predicate has no reachable <c>IS NULL</c>/<c>IS NOT NULL</c> test anywhere against that
    /// column. SQL Server's three-valued logic means a comparison against <c>NULL</c> evaluates to
    /// UNKNOWN, never FALSE, and a CHECK constraint only rejects a row when its predicate evaluates
    /// to FALSE - UNKNOWN passes, exactly like TRUE. Oracle-confirmed directly (Docker instance,
    /// disposable scratch database, dropped immediately after): a nullable <c>Price</c> column with
    /// <c>CHECK (Price &gt; 0)</c> genuinely rejected <c>INSERT ... VALUES (-5)</c> (Msg 547) but
    /// genuinely ACCEPTED <c>INSERT ... VALUES (NULL)</c> with no error at all - the same constraint
    /// silently doing nothing for the one value a naive reading of "Price must be positive" would
    /// most expect it to catch. <see cref="FindingConfidence.High"/>, SARIF Error: this is not a
    /// magnitude estimate or a workload-dependent risk the way a structural-cost finding is - it is
    /// a guaranteed, unconditional gap in this exact constraint's own enforcement against any current
    /// or future <c>NULL</c> row, the same "provably wrong right now, not merely likely" certainty
    /// tier <see cref="NotInNullableSubqueryFinding"/> already uses for an analogous three-valued-
    /// logic gap. AND-only-reachable discipline, but INVERTED from most other scanners in this
    /// codebase that use it: those (<see cref="CompositeIndexLeadingColumnScanner"/>,
    /// <see cref="NotInNullableSubqueryScanner"/>) require a violating condition to be AND-reachable
    /// before triggering, and treat "referenced anywhere, OR branches included" as liberal grounds to
    /// SUPPRESS. This kind's own risk is the ABSENCE of a NULL guard, so the liberal, OR-inclusive
    /// collection is what suppresses it here too: a guard hidden inside an OR branch
    /// (<c>CHECK (Price IS NULL OR Price &gt; 0)</c>, the textbook correct fix) still counts as
    /// "handled" and must never fire - checked directly against the same oracle: that exact rewritten
    /// predicate accepted the <c>NULL</c> row AND still rejected <c>-5</c>, confirming both the
    /// original bug and the standard fix. Only fires when the checked column is itself nullable (a
    /// <c>NOT NULL</c> column makes the concern moot by construction) and the predicate has no
    /// <c>IS NULL</c>/<c>IS NOT NULL</c> test against it anywhere in the whole parsed predicate tree,
    /// not merely along one path. Distinct from the already-shipped <see
    /// cref="UntrustedConstraintFindingKind.CheckConstraint"/> stream: that one is about the engine no
    /// longer trusting an otherwise-correct constraint (<c>WITH NOCHECK</c> re-enablement); this one
    /// is about the constraint's own predicate text being wrong from the moment it was written,
    /// independent of trust state.
    /// </summary>
    NullNotHandled,

    /// <summary>
    /// docs/detection-checklist.md Tier 2 §A "CHECK constraint accidentally placed on an IDENTITY
    /// column" - a numeric-threshold CHECK directly referencing the same column an IDENTITY
    /// specification generates values for. Oracle-confirmed directly (Docker instance, disposable
    /// scratch database, dropped immediately after): an IDENTITY(1,1) column with
    /// <c>CHECK (Id &gt; 5)</c> rejected every insert (Msg 547) for identity values 1 through 5 -
    /// four back-to-back inserts all failed - and the identity counter kept ADVANCING through every
    /// rejected attempt exactly as documented IDENTITY behavior (the value is generated before
    /// constraint evaluation, and a failed statement never gives it back), so the table was left with
    /// a 5-value gap before the fifth insert finally succeeded at Id = 6. Every insert against a
    /// freshly-created table with this shape is guaranteed to fail deterministically until the
    /// auto-generated counter happens to satisfy the predicate on its own, then the failures stop
    /// forever with no code change - exactly the "fails in dev, catch-before-prod" shape CLAUDE.md's
    /// own precision-first bar exists for. <see cref="FindingConfidence.High"/>, SARIF Error: the
    /// counter-vs-threshold mechanics are mechanical, oracle-confirmed engine behavior with no
    /// workload-dependence at all (unlike e.g. <see
    /// cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>'s genuinely workload-dependent
    /// contention claim) - the constraint is either already satisfied by the current counter value
    /// (in which case it is provably a no-op, since IDENTITY never decreases) or it is provably still
    /// rejecting every insert, and either way the constraint can never do the job its own predicate
    /// text suggests: gating genuinely bad data rather than an accident of insertion order. Catalog-
    /// decidable purely from <see cref="Catalog.CatalogColumn.IsIdentity"/> plus the CHECK
    /// constraint's own definition text referencing that same column - no AST reachability nuance
    /// needed here, unlike <see cref="NullNotHandled"/>, since an IDENTITY column is always
    /// <c>NOT NULL</c> by construction and this kind fires on simple column reference, not a missing
    /// NULL guard.
    /// </summary>
    ConstraintOnIdentityColumn,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 §A, the CHECK-constraint-text-correctness pair - both kinds
/// catalog-decidable from <see cref="Catalog.CatalogCheckConstraint.DefinitionText"/> (reparsed
/// through the same throwaway-wrapper-statement technique <see cref="SchemaDependencyScanner"/>
/// already uses for the identical text) plus <see cref="Catalog.CatalogColumn.IsNullable"/>/
/// <see cref="Catalog.CatalogColumn.IsIdentity"/>, so one shared finding shape covers both rather
/// than fragmenting into two near-identical types - this codebase's established "one Kind
/// discriminator" convention (<see cref="ControlFlowRiskFinding"/>/<see cref="IndexDesignFinding"/>).
/// Live-mode only by construction, the same reasoning <see cref="UntrustedConstraintFinding"/>
/// already documents: <see cref="Catalog.CatalogCheckConstraint.DefinitionText"/> is populated only
/// by <c>LiveCatalogReader</c> (file mode never replicates the engine's own CHECK-definition
/// canonicalization, matching CLAUDE.md's "do not invest in file-parsed DDL fidelity" line), so this
/// stream runs inline inside <see cref="Reporting.ScanReportBuilder"/> exactly like <see
/// cref="UntrustedConstraintScanner"/> does - no separate <c>LiveScanRunner</c> merge step is needed,
/// since the definition text is already sitting in <see cref="Catalog.DatabaseCatalog.CheckConstraints"/>
/// by the time the builder runs for both a live <c>scan-db</c> target and a deployed corpus repo.
///
/// <b>Origin attribution is a known, deliberately deferred gap</b>, identical to <see
/// cref="UntrustedConstraintFinding"/>'s own documented one: no DDL-statement-to-(file,line)
/// side-channel exists for a live-read constraint, so <see cref="SourcePath"/>/<see cref="Line"/>
/// fall back to the owning table's own declaration site.
/// </summary>
public sealed record CheckConstraintFinding(
    CheckConstraintFindingKind Kind,
    string ConstraintName,
    string TableQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

