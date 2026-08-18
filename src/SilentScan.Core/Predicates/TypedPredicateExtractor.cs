using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3+4: finds comparison predicates in WHERE/ON/HAVING/BETWEEN across procs, views,
/// functions, and ad-hoc statements, resolves the column side through the catalog/lineage,
/// types the other side, and classifies the verdict (CLAUDE.md Pass 3 + Pass 4).
/// </summary>
public static class TypedPredicateExtractor
{
    // externalVariables: variable/parameter types known before parsing even starts - used by
    // DynamicSqlPipeline to seed sp_executesql's declared parameter types (CLAUDE.md dynamic
    // SQL policy, Tier B), since those are declared at the call site, not inside the reparsed
    // query text itself. Null/empty for ordinary static SQL. enclosingScope: the proc/function/
    // trigger a reparsed dynamic SQL fragment was found inside, if any - lets a #temp table or
    // trigger inserted/deleted pseudo-table that resolves fine in the surrounding STATIC body
    // resolve inside the dynamic text too, since the reparsed fragment has no CREATE PROCEDURE
    // wrapper of its own to discover either from.
    public static PredicateExtractionResult Extract(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SqlType?>? externalVariables = null,
        DynamicSqlScope? enclosingScope = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
    {
        var resolvedViews = lineage.AllRelations;
        var ledger = new SkipLedger();
        var visitor = new Visitor(parseResult.SourcePath, catalog, resolvedViews, externalVariables, ledger, enclosingScope, callerScopeByCalleeScope);
        visitor.SeedEnclosingScope(parseResult.Fragment);
        parseResult.Fragment.Accept(visitor);
        return new PredicateExtractionResult(visitor.Findings, visitor.ExpressionDerivedFindings, visitor.CollationConflictFindings, visitor.WriteLossFindings, ledger.Entries, visitor.OversizedParameterFindings, visitor.UnderLengthParameterFindings, visitor.AnsiPaddingMismatchFindings, visitor.LocalVariablePredicateFindings, visitor.FilteredIndexParameterMismatchFindings);
    }

    // CS9107: sourcePath/catalog/resolvedViews/ledger are used throughout this class's own body
    // (way beyond scope resolution - literal typing, catalog column lookups, finding
    // construction, ...) AND forwarded to ScopedSqlVisitorBase for its own, separate CTE/scope
    // bookkeeping - a deliberate, harmless double capture, not the accidental one this warning
    // exists to catch. Splitting the base's storage out into protected accessors instead would
    // mean rewriting every one of this class's own (non-scope-related) usages of these four
    // parameters to go through the base, which is real, separate, higher-risk work, not a
    // byproduct of extracting the scope harness.
#pragma warning disable CS9107
    private sealed class Visitor(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, SqlType?>? externalVariables,
        SkipLedger ledger,
        DynamicSqlScope? enclosingScope = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
        : ScopedSqlVisitorBase(sourcePath, catalog, resolvedViews, ledger, enclosingScope?.ProcScope, callerScopeByCalleeScope)
#pragma warning restore CS9107
    {
        /// <summary>Skip-ledger construct kind shared by every "this operand has no type resolution" entry below - one label for the whole family of unresolved-operand reasons.</summary>
        private const string PredicateOperandConstructKind = "predicate operand";

        /// <summary>Skip-ledger construct kind shared by every operator that is oracle-verified non-sargable regardless of type match (&lt;&gt;, NOT LIKE, NOT IN, &lt;&gt; ALL) - the type-conversion verdict machinery never applies to these.</summary>
        private const string NonSeekableOperatorConstructKind = "non-seekable operator";

        /// <summary>Skip-ledger construct kind for an INSERT whose target table/column doesn't resolve against the catalog - write-loss analysis has nothing to compare against.</summary>
        private const string WriteTargetConstructKind = "write target";

        /// <summary>Skip-ledger construct kind for an INSERT source shape Phase E1 doesn't analyze (anything but a plain VALUES list or a single non-UNION SELECT with an explicit scalar select list).</summary>
        private const string WriteSourceConstructKind = "write source";

        /// <summary>Skip-ledger construct kind for a comparison found nested inside a CASE/IIF/COALESCE/NULLIF branch that sits within an enclosing filter clause - see <see cref="EnterOperandPosition"/>.</summary>
        private const string OperandPositionConstructKind = "comparison inside scalar expression";

        private const string OperandPositionLedgerReason = "not a seek position - nested inside a CASE/IIF/COALESCE/NULLIF branch (or similar operand position) within an enclosing filter clause";

        /// <summary>Skip-ledger construct kind for a comparison where neither side resolved to a real column - most commonly a column wrapped in COALESCE/CASE/NULLIF/IIF on both sides, or against another wrapped expression. Genuinely benign: neither side was ever a bare column reference in the first place.</summary>
        private const string NoColumnOperandConstructKind = "no column operand";

        /// <summary>
        /// Skip-ledger construct kind for a comparison where at least one side IS syntactically a
        /// bare <see cref="ColumnReferenceExpression"/> yet still failed to resolve to a
        /// <see cref="PredicateOperand.Column"/> - unlike <see cref="NoColumnOperandConstructKind"/>,
        /// this is a genuine analysis gap (an unresolved FROM-scope reference, most commonly), not
        /// a benign "both sides are expressions" shape. Splitting this out keeps
        /// <c>SkippedConstructSummary</c>'s "no column operand" bucket honestly reserved for the
        /// harmless case, per CLAUDE.md's "never silently counted as clean" - a two-sided
        /// unresolved-column comparison used to fall into the exact same bucket as `expr = expr`
        /// with no way to tell them apart in the published study's honesty numbers.
        /// </summary>
        private const string UnresolvedColumnComparisonConstructKind = "unresolved column comparison";

        /// <summary>Pushes the enclosing trigger's inserted/deleted pseudo-tables onto the CTE stack, if any - called once, before the visitor starts walking, so they're visible for the whole reparsed fragment exactly like a real trigger body's own VisitTriggerBody does.</summary>
        public void SeedEnclosingScope(TSqlFragment rootFragment)
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                PushCteRelations(BuildTriggerPseudoTableRelations(target, rootFragment));
            }
        }

        /// <summary>
        /// Mirrors NonSargablePredicateScanner's identical tracker (CLAUDE.md Tier-1 scope note:
        /// "never a SELECT list, ORDER BY, or GROUP BY - there's no seek to lose"): a comparison
        /// that never filters rows isn't a verdict-bearing finding either, e.g. a CASE expression
        /// in a SELECT list comparing a column to a literal. Before the boolean pair this enum
        /// replaced existed, TypedPredicateExtractor had no such gating at all and reported a
        /// ScanForced/RangeSeek verdict for ANY comparison anywhere in the tree, filter or not.
        /// <see cref="SuppressedOperand"/> is a second, independent fact about the SAME node
        /// (not extra state a single flag couldn't represent - every read site only ever
        /// consults it while already in the <see cref="NotSeekable"/> branch): not "are we
        /// filtering now" but "did we just leave an active filter to enter this operand
        /// position" - used only to decide the ledger message, distinguishing a comparison
        /// that's surprisingly not a seek predicate (textually inside a WHERE, but a CASE
        /// WHEN-condition the optimizer never uses as one) from a comparison that was simply
        /// never near a filter clause to begin with (silently excluded, unchanged).
        /// </summary>
        private enum PredicatePosition
        {
            /// <summary>Not a filtering position at all - a SELECT-list expression, ORDER BY, GROUP BY, or similar. Never ledgered; matches Tier-1's identical silent exclusion.</summary>
            NotSeekable,

            /// <summary>A genuine WHERE/HAVING/ON/MERGE-condition position - eligible for a verdict.</summary>
            Seekable,

            /// <summary>A CASE/IIF/COALESCE/NULLIF operand position reached by suspending an ENCLOSING active <see cref="Seekable"/> position (see <see cref="EnterOperandPosition"/>) - ledgered rather than silent, since this one is textually inside a filter clause.</summary>
            SuppressedOperand,
        }

        private PredicatePosition _position;

        // Roadmap Phase E2: `WHERE NOT (Col = @p)` previously visited the inner
        // BooleanComparisonExpression completely unaware of the enclosing NOT - the default
        // ExplicitVisit(BooleanNotExpression) recurses into .Expression with no polarity
        // tracking at all, so this reported a plain `=` finding for what the engine actually
        // sees as `<>` (a materially different, oracle-verified-non-sargable comparison,
        // CLAUDE.md's own precision discipline turned against itself: a WRONG verdict, not a
        // missing one). A bool rather than a stack because BooleanNotExpression nests by
        // toggling parity (NOT NOT X == X), never by depth.
        private bool _negated;

        /// <summary>
        /// Roadmap Phase E3: the whole boolean predicate fragment currently being resolved (set
        /// right before each top-level predicate visitor's own ResolveOperand call(s), read only
        /// by RecordExpressionDerivedFinding) - lets an expression-derived finding carry the
        /// exact comparison it was found inside of, re-rendered to valid T-SQL text, so the
        /// corpus oracle can actually probe it. Never needs restoring the way _negated does:
        /// each predicate visitor is a leaf call (it never itself recurses into another nested
        /// predicate visit before its own ResolveOperand calls return), so the next Visit(...)
        /// simply overwrites it.
        /// </summary>
        private TSqlFragment? _currentPredicateFragment;

        private readonly Dictionary<string, SqlType?> _variables = externalVariables is null
            ? new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SqlType?>(externalVariables, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Which of <see cref="_variables"/>'s keys is a formal parameter (a real
        /// <c>CREATE PROCEDURE</c>/<c>FUNCTION</c> parameter, or an <c>sp_executesql</c> parameter
        /// seeded via <c>externalVariables</c> - both are genuinely caller-supplied
        /// per execution, unlike a <c>DECLARE</c>) rather than a plain local - docs/detection-
        /// checklist.md Tier 2 "Local-variable predicates": a predicate against a local variable's
        /// value is invisible to the cardinality estimator (falls back to the column's average-
        /// density statistic) in a way a formal parameter's sniffed value is not. Cleared at
        /// exactly the same three sites <see cref="_variables"/> itself is cleared, so the two
        /// never drift out of sync.
        /// </summary>
        private readonly HashSet<string> _formalParameterNames = externalVariables is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(externalVariables.Keys, StringComparer.OrdinalIgnoreCase);

        public List<TypedPredicateFinding> Findings { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerivedFindings { get; } = [];

        public List<CollationConflictFinding> CollationConflictFindings { get; } = [];

        public List<WriteLossFinding> WriteLossFindings { get; } = [];

        public List<OversizedParameterFinding> OversizedParameterFindings { get; } = [];

        public List<UnderLengthParameterFinding> UnderLengthParameterFindings { get; } = [];

        public List<AnsiPaddingMismatchFinding> AnsiPaddingMismatchFindings { get; } = [];

        public List<LocalVariablePredicateFinding> LocalVariablePredicateFindings { get; } = [];

        public List<FilteredIndexParameterMismatchFinding> FilteredIndexParameterMismatchFindings { get; } = [];

        /// <summary>
        /// docs/detection-checklist.md full-archive practitioner sweep §E, "Filtered index whose
        /// predicate compares against a variable/parameter, not a literal" - built once, lazily
        /// (most scanned corpora have few or no filtered indexes at all, so most callers never pay
        /// for this): every table's filtered index whose own filter reparses as a simple
        /// <c>Column = Literal</c> equality (<see cref="IndexDesignScanner.TryExtractSimpleLiteralEqualityFilter"/>),
        /// keyed by <c>(TableQualifiedName, ColumnName)</c> ordinal-ignore-case on both parts - the
        /// same key shape <see cref="TryAddFilteredIndexParameterMismatchFinding"/> looks up against
        /// a real predicate site's own resolved column.
        /// </summary>
        private Dictionary<(string Table, string Column), List<(string? IndexName, string LiteralText)>>? _literalEqualityFilteredIndexesByColumn;

        private Dictionary<(string Table, string Column), List<(string? IndexName, string LiteralText)>> LiteralEqualityFilteredIndexesByColumn
        {
            get
            {
                if (_literalEqualityFilteredIndexesByColumn is not null)
                {
                    return _literalEqualityFilteredIndexesByColumn;
                }

                var map = new Dictionary<(string, string), List<(string?, string)>>(TableColumnKeyComparer.Instance);
                foreach (var table in catalog.Tables)
                {
                    foreach (var index in table.Indexes)
                    {
                        if (!index.IsFiltered || index.FilterDefinition is not { } filterDefinition
                            || IndexDesignScanner.TryExtractSimpleLiteralEqualityFilter(filterDefinition) is not { } extracted)
                        {
                            continue;
                        }

                        var key = (table.QualifiedName, extracted.ColumnName);
                        if (!map.TryGetValue(key, out var entries))
                        {
                            entries = [];
                            map[key] = entries;
                        }

                        entries.Add((index.Name, extracted.LiteralText));
                    }
                }

                _literalEqualityFilteredIndexesByColumn = map;
                return map;
            }
        }

        private sealed class TableColumnKeyComparer : IEqualityComparer<(string Table, string Column)>
        {
            public static readonly TableColumnKeyComparer Instance = new();

            public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
                string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Table, string Column) obj) =>
                HashCode.Combine(obj.Table.ToUpperInvariant(), obj.Column.ToUpperInvariant());
        }

        /// <summary>
        /// True for the whole body of a procedure declared <c>WITH RECOMPILE</c>
        /// (<c>ProcedureStatementBody.Options</c> containing <c>ProcedureOptionKind.Recompile</c> -
        /// functions/triggers can never carry this option, so it's reset to false whenever their
        /// own body-visiting helper runs) - every predicate in such a body recompiles against real,
        /// current values on every call, fully neutralizing both <see
        /// cref="LocalVariablePredicateFindings"/>'s and the catch-all stream's own premise.
        /// </summary>
        private bool _procedureHasWithRecompile;

        /// <summary>
        /// True only inside the single top-level statement (a subquery/derived table is never its
        /// own <c>SelectStatement</c>/<c>UpdateStatement</c>/etc in ScriptDOM's object model - it
        /// wraps a bare <c>QueryExpression</c> - so this never needs a stack, just save/restore
        /// around the one statement kind that can carry <c>OPTION (RECOMPILE)</c>) that carries an
        /// explicit <c>OPTION (RECOMPILE)</c> query hint.
        /// </summary>
        private bool _statementHasOptionRecompile;

        private bool HasActiveRecompileGuard => _procedureHasWithRecompile || _statementHasOptionRecompile;

        /// <summary>
        /// Set by <see cref="AnalyzeInsertWriteLoss"/> just before <see cref="ExplicitVisit(InsertStatement)"/>
        /// walks the InsertSpecification, and consumed (cleared) by the very next
        /// <see cref="ExplicitVisit(QuerySpecification)"/> - which is always that exact SELECT,
        /// since InsertSpecification's Target/Columns contain no QuerySpecification of their own
        /// to hit first, and the WITH clause's own CTE bodies (which DO contain QuerySpecifications)
        /// are walked separately, before these fields are ever set. Consuming it before that
        /// override recurses into its OWN FromClause/select list (which may contain a derived-table
        /// subquery, itself a QuerySpecification) is what keeps a nested subquery from wrongly
        /// stealing these fields instead.
        /// </summary>
        private IReadOnlyList<CatalogColumn?>? _pendingInsertTargetColumns;

        private string? _pendingInsertTargetTable;

        public override void ExplicitVisit(SelectStatement node)
        {
            // A WITH clause's CTEs are visible for the whole statement they're declared in, not
            // scoped per nested QuerySpecification (docs/audit-remediation-plan.md Phase 2.4). A
            // statement with no WITH clause of its own still sees any outer statement's CTEs (a
            // derived-table subquery nested inside a CTE-using statement), so this always pushes
            // something - an unchanged copy of the current top when there's nothing new to add.
            PushCteScope(node.WithCtesAndXmlNamespaces);
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            ScopeStack.Push(FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext()));

            // Consumed here, before any recursion at all (a derived-table subquery inside this
            // SELECT's own FROM/select list is itself a QuerySpecification, and must never see
            // these still set).
            if (_pendingInsertTargetColumns is { } pendingColumns)
            {
                _pendingInsertTargetColumns = null;
                var pendingTable = _pendingInsertTargetTable!;
                _pendingInsertTargetTable = null;
                AnalyzeSelectListWriteLoss(node.SelectElements, pendingColumns, pendingTable);
            }

            // Reset to false for every part of this query specification except its own WHERE/
            // HAVING (whose own overrides below turn it back on) - without this, an outer
            // WHERE's own nested subquery (an EXISTS/IN (SELECT ...)) would inherit "filter
            // context = true" for that subquery's unrelated SELECT list, and a top-level SELECT
            // list would inherit whatever the enclosing scope happened to be.
            var previousPosition = _position;
            _position = PredicatePosition.NotSeekable;

            // `_negated` is reset here too, for the identical reason: `WHERE NOT EXISTS (SELECT
            // ... WHERE a.x = b.y)` previously visited the inner `a.x = b.y` with `_negated` still
            // true from the outer NOT, wrongly negating it to `<>` and routing a real predicate to
            // the non-seekable-operator ledger skip instead of classifying it. The NOT visually
            // wraps the whole EXISTS(...), not the subquery's own, independent boolean structure -
            // a nested query specification is a fresh scope for negation polarity exactly like it
            // already is for filter position.
            var previousNegated = _negated;
            _negated = false;

            node.FromClause?.Accept(this);
            foreach (var element in node.SelectElements)
            {
                element.Accept(this);
            }

            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.HavingClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            node.WindowClause?.Accept(this);

            _negated = previousNegated;
            _position = previousPosition;
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _position;
            _position = PredicatePosition.Seekable;
            node.AcceptChildren(this);
            _position = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _position;
            _position = PredicatePosition.Seekable;
            node.AcceptChildren(this);
            _position = previous;
        }

        /// <summary>A JOIN's ON clause is a filter context exactly like WHERE; the table references it joins are not (a derived-table subquery there has its own SELECT list to protect).</summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _position;
            _position = PredicatePosition.Seekable;
            node.SearchCondition?.Accept(this);
            _position = previous;
        }

        // UPDATE/DELETE/MERGE previously pushed no FROM scope at all, so every predicate in
        // their WHERE/ON clause hit the empty-scope early return in TryAddFinding and vanished -
        // in OLTP procedures this is where a large share of index-killing predicates live
        // (docs/audit-remediation-plan.md Phase 4.1, audit finding B1: "the single biggest
        // coverage gap in the tool"). Each pushes CTEs the same way ExplicitVisit(SelectStatement)
        // does - DataModificationStatement shares the same WithCtesAndXmlNamespaces base type.
        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        // Roadmap Phase E1: write-side (INSERT) analysis is additive to the WHERE/ON/HAVING
        // predicate scanning above - no FROM scope is pushed onto ScopeStack for the target
        // table itself (unlike UPDATE/DELETE/MERGE, an INSERT target is never referenced by a
        // predicate; its own catalog columns are looked up directly by AnalyzeInsertWriteLoss),
        // only CTEs, so a CTE referenced by an INSERT ... SELECT source still resolves.
        public override void ExplicitVisit(InsertStatement node)
        {
            var spec = node.InsertSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);

            // The WITH clause's CTE bodies are QuerySpecifications too, and the natural child walk
            // reaches them BEFORE reaching InsertSpecification. Walk the WITH clause first (so
            // CTE-body QuerySpecifications see no pending-insert fields and can't be mis-consumed
            // as the INSERT's SELECT - which is what an `INSERT ... WITH cte AS (SELECT * FROM
            // ...) SELECT ... FROM cte` shape hit, invalid-casting the CTE's SelectStarExpression
            // to SelectScalarExpression). AnalyzeInsertWriteLoss then sets the pending fields for
            // the next QuerySpecification, which is guaranteed to be the InsertSource's own SELECT.
            node.WithCtesAndXmlNamespaces?.Accept(this);
            AnalyzeInsertWriteLoss(spec);
            spec.Accept(this);

            PopCteScope();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));
            var previousStatementHasOptionRecompile = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previousStatementHasOptionRecompile;
            ScopeStack.Pop();
            PopCteScope();
        }

        /// <summary>
        /// MergeSpecification's ON clause (SearchCondition) is a genuine filter position - a raw
        /// BooleanExpression, not wrapped in a WhereClause node the way SELECT/UPDATE/DELETE's
        /// WHERE is, so it needs its own filter-context toggle here rather than picking one up
        /// from an ExplicitVisit(WhereClause) override. Each action clause's own "AND &lt;cond&gt;"
        /// extra condition (delegated to ExplicitVisit(MergeActionClause) below) is too. The
        /// action BODY (an UPDATE's SET clauses, an INSERT's VALUES list) is NOT: it's an
        /// assignment/projection position, structurally the same as a SELECT list.
        ///
        /// The previous version of this held filter-context true across the WHOLE
        /// MergeSpecification subtree (rationale: "no SELECT-list analog anywhere inside a
        /// MergeSpecification") - that was wrong. UpdateMergeAction.SetClauses and
        /// InsertMergeAction's VALUES list ARE exactly that analog: a CASE expression inside
        /// either (`WHEN MATCHED THEN UPDATE SET x = CASE WHEN t.Code = N'x' THEN 1 ELSE 0 END`)
        /// used to leak its inner WHEN comparison out as a false predicate finding, since default
        /// traversal reaches it with filter-context still held true from the MERGE statement.
        /// </summary>
        public override void ExplicitVisit(MergeSpecification node)
        {
            node.Target?.Accept(this);
            node.TableReference?.Accept(this);
            node.TopRowFilter?.Accept(this);

            var previousPosition = _position;
            _position = PredicatePosition.Seekable;
            node.SearchCondition?.Accept(this);
            _position = previousPosition;

            foreach (var actionClause in node.ActionClauses)
            {
                actionClause.Accept(this);
            }

            node.OutputClause?.Accept(this);
            node.OutputIntoClause?.Accept(this);
        }

        /// <summary>Splits a WHEN [NOT] MATCHED clause's own "AND &lt;cond&gt;" extra condition (a filter position) from its action body (an assignment/projection position) - see <see cref="ExplicitVisit(MergeSpecification)"/>.</summary>
        public override void ExplicitVisit(MergeActionClause node)
        {
            var previousPosition = _position;
            _position = PredicatePosition.Seekable;
            node.SearchCondition?.Accept(this);
            _position = previousPosition;

            node.Action?.Accept(this);
        }

        // Roadmap Phase E1: UPDATE ... SET's own targets, previously never visited at all (only
        // its WHERE clause was). The scope UPDATE's own ExplicitVisit above already pushed has
        // the target table (and any extra FROM tables) live, so both Column and NewValue resolve
        // through the exact same ResolveOperand every predicate operand goes through - a
        // multi-table `UPDATE t SET t.Col = s.Col FROM t JOIN s ON ...` types NewValue against
        // s.Col correctly, not just literals. node.Variable ( SET @v = ... ) is not a write to any
        // catalog column, so it is silently skipped rather than ledgered - it was never something
        // this pass could have analyzed in the first place, not an analysis attempt that failed.
        public override void ExplicitVisit(AssignmentSetClause node)
        {
            if (node.Column is { } columnRef)
            {
                var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
                if (ResolveOperand(columnRef, scopeChain) is PredicateOperand.Column target && target.Type is { } targetType)
                {
                    var sourceType = OperandType(ResolveOperand(node.NewValue, scopeChain));
                    EmitWriteLossFinding(target.TableQualifiedName, target.ColumnName, targetType, sourceType, node.NewValue);
                }
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// DECLARE'd variable types are batch-scoped in real T-SQL (a `GO`-separated batch
        /// starts with none) - without this, an ad-hoc batch with no CREATE PROCEDURE/FUNCTION/
        /// TRIGGER wrapper (so none of VisitProcedureOrFunctionBody/VisitTriggerBody's own
        /// _variables.Clear() ever fires) inherited whatever an EARLIER batch in the same file
        /// happened to DECLARE, and a later reference to a same-named variable with no DECLARE
        /// of its own silently typed itself from that stale, unrelated batch instead of Unknown.
        /// Resets to exactly the externally-seeded set (DynamicSqlPipeline's sp_executesql
        /// parameter seeding) rather than an unconditional clear - a reparsed dynamic-SQL
        /// fragment is always exactly one batch (real T-SQL rejects GO inside a string executed
        /// via EXEC()/sp_executesql, so this can never fire more than once for that case anyway),
        /// and those seeded parameters must still be visible for it.
        /// </summary>
        public override void ExplicitVisit(TSqlBatch node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
            if (externalVariables is not null)
            {
                foreach (var (name, type) in externalVariables)
                {
                    _variables[name] = type;
                    _formalParameterNames.Add(name);
                }
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variables[declaration.VariableName.Value] = SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Toggles negation parity around whatever this NOT wraps, rather than dispatching to a
        /// specific predicate kind - AcceptChildren still reaches the SAME BooleanComparisonExpression/
        /// LikePredicate/InPredicate visitors below, now with <see cref="_negated"/> correctly
        /// reflecting an odd or even number of enclosing NOTs (NOT NOT X negates twice, back to
        /// X's own polarity).
        /// </summary>
        public override void ExplicitVisit(BooleanNotExpression node)
        {
            _negated = !_negated;
            node.AcceptChildren(this);
            _negated = !_negated;
        }

        // A CASE/IIF branch is a scalar OPERAND position, not a seek position, even when the
        // CASE/IIF itself sits inside an active filter clause: `WHERE CASE WHEN Col = N'X' THEN
        // 1 ELSE 0 END = 1` has exactly one seekable predicate (the outer `= 1` comparison,
        // already resolved through ResolveOperand's own ExpressionTypeInferencer branch) - the
        // inner `Col = N'X'` is a WHEN-condition ScriptDom's default traversal still walks into
        // (AcceptChildren recurses into WhenClauses regardless of what ResolveOperand already
        // did with this same node), and it was previously visited with the position still
        // true, reporting a verdict for a comparison the optimizer never uses as a seek
        // predicate. SimpleCaseExpression/CoalesceExpression/NullIfExpression can't syntactically
        // embed a bare BooleanComparisonExpression the way SearchedCaseExpression/IIfCall can,
        // but are suspended here too for uniformity with "any of these is an operand position",
        // matching CLAUDE.md's own named hard-case list.
        public override void ExplicitVisit(SearchedCaseExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(SimpleCaseExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(IIfCall node) => EnterOperandPosition(node);

        public override void ExplicitVisit(CoalesceExpression node) => EnterOperandPosition(node);

        public override void ExplicitVisit(NullIfExpression node) => EnterOperandPosition(node);

        /// <summary>Shared by every top-level statement kind that can carry <c>OPTION (...)</c> query hints - a subquery/derived table is never its own SelectStatement/UpdateStatement/etc in ScriptDOM's object model, so this never needs a stack.</summary>
        private bool BeginStatementOptimizerHints(IList<OptimizerHint> hints)
        {
            var previous = _statementHasOptionRecompile;
            _statementHasOptionRecompile = hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);
            return previous;
        }

        /// <summary>
        /// Suspends the seekable position (and negation) while walking a scalar-expression
        /// OPERAND subtree (a CASE/IIF/COALESCE/NULLIF branch) via ScriptDom's own default
        /// traversal - anything found in there is a value being computed, not a row filter,
        /// exactly like a SELECT-list expression. Moves to <see cref="PredicatePosition.SuppressedOperand"/>
        /// only when there was a real, active <see cref="PredicatePosition.Seekable"/> position to
        /// suspend (not merely absent to begin with) - otherwise <see cref="PredicatePosition.NotSeekable"/>,
        /// so the read sites can tell "genuinely never in a filter" (stay silent, unchanged) apart
        /// from "was in a filter, then descended into an operand position" (ledger it - textually
        /// inside a WHERE is a surprising place for a comparison to turn out not to be a seek
        /// predicate).
        /// </summary>
        private void EnterOperandPosition(TSqlFragment node)
        {
            var previousPosition = _position;
            _position = previousPosition == PredicatePosition.Seekable ? PredicatePosition.SuppressedOperand : PredicatePosition.NotSeekable;
            node.AcceptChildren(this);
            _position = previousPosition;
        }

        /// <summary>
        /// True (the caller should stop, having already ledgered when appropriate) whenever the
        /// current position isn't <see cref="PredicatePosition.Seekable"/> - shared by every
        /// predicate-kind visitor (IN, ANY/SOME/ALL, and the common `=`/`&lt;&gt;`/range-operator
        /// path in <see cref="TryAddFinding"/>) that has no operator-specific "not sargable
        /// regardless of type match" case of its own to report first (unlike NOT BETWEEN/NOT LIKE
        /// above, which ledger a different reason when genuinely in a seekable position).
        /// </summary>
        private bool SkipIfNotSeekable(TSqlFragment node)
        {
            if (_position == PredicatePosition.Seekable)
            {
                return false;
            }

            if (_position == PredicatePosition.SuppressedOperand)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
            }

            return true;
        }

        /// <summary>
        /// Resolves an INSERT target that is a plain named table/view, then dispatches to the
        /// shape-specific analysis for whatever its InsertSource turns out to be. A target that
        /// isn't a <see cref="NamedTableReference"/> at all (never legal SQL for INSERT's own
        /// Target in practice) or doesn't resolve in the catalog is ledgered once here rather
        /// than guessed at.
        /// </summary>
        private void AnalyzeInsertWriteLoss(InsertSpecification spec)
        {
            var table = ResolveWriteTargetTable(spec.Target);
            if (table is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, spec.Target.StartLine, spec.Target.StartColumn,
                    WriteTargetConstructKind, "INSERT target does not resolve to a known table - write-loss analysis skipped");
                return;
            }

            var targetColumns = ResolveInsertTargetColumns(spec, table);

            switch (spec.InsertSource)
            {
                case ValuesInsertSource values:
                    AnalyzeValuesInsertSource(values, targetColumns, table.QualifiedName);
                    return;

                // The target column list is stashed for the SELECT this InsertSource wraps to
                // pick up once ExplicitVisit(QuerySpecification) reaches it and its own FROM
                // scope is live - see _pendingInsertTargetColumns' own doc comment. Only a plain,
                // non-UNION SELECT with an explicit scalar select list (no `SELECT *`) is
                // supported; anything else is ledgered below.
                case SelectInsertSource { Select: QuerySpecification querySpec }
                    when querySpec.SelectElements.All(e => e is SelectScalarExpression):
                    _pendingInsertTargetColumns = targetColumns;
                    _pendingInsertTargetTable = table.QualifiedName;
                    return;

                default:
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, spec.StartLine, spec.StartColumn,
                        WriteSourceConstructKind, $"INSERT source of kind '{spec.InsertSource.GetType().Name}' is not analyzed for write-loss - only a plain VALUES list or a single non-UNION SELECT with an explicit scalar select list is");
                    return;
            }
        }

        private CatalogTable? ResolveWriteTargetTable(TableReference target)
        {
            if (target is not NamedTableReference named)
            {
                return null;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            var table = catalog.Find(qualifiedName, CurrentProcScope);

            // Same "#temp is session-scoped, not proc-scoped" fallback ResolveNamedTableReference
            // uses for a SELECT's own FROM clause - an INSERT into a #temp table created by one
            // of this proc's known callers is exactly as common a pattern as querying one.
            if (table is null && CurrentProcScope is not null
                && callerScopeByCalleeScope is not null
                && callerScopeByCalleeScope.TryGetValue(CurrentProcScope, out var callerScopes))
            {
                table = FromScopeResolver.TryResolveFromCallerScopes(catalog, qualifiedName, callerScopes);
            }

            return table;
        }

        /// <summary>
        /// An explicit column list matches positionally by name against the target table's real
        /// columns; an omitted one (<c>INSERT INTO T VALUES (...)</c>) means every one of the
        /// table's own columns, in declared order. A named column that doesn't resolve on the
        /// table is ledgered and represented as a null placeholder so its position still lines up
        /// with the corresponding VALUES/SELECT entry - <see cref="AnalyzeValuesInsertSource"/>
        /// and <see cref="AnalyzeSelectListWriteLoss"/> both just skip a null entry.
        /// </summary>
        private List<CatalogColumn?> ResolveInsertTargetColumns(InsertSpecification spec, CatalogTable table)
        {
            if (spec.Columns.Count == 0)
            {
                return [.. table.Columns];
            }

            var resolved = new List<CatalogColumn?>(spec.Columns.Count);
            foreach (var columnRef in spec.Columns)
            {
                var name = columnRef.MultiPartIdentifier.Identifiers[^1].Value;
                var column = table.FindColumn(name);
                if (column is null)
                {
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                        WriteTargetConstructKind, $"INSERT target column '{name}' does not resolve on table '{table.QualifiedName}' - write-loss analysis skipped for this column");
                }

                resolved.Add(column);
            }

            return resolved;
        }

        private void AnalyzeValuesInsertSource(ValuesInsertSource values, List<CatalogColumn?> targetColumns, string targetTableQualifiedName)
        {
            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            foreach (var columnValues in values.RowValues.Select(row => row.ColumnValues))
            {
                var count = Math.Min(columnValues.Count, targetColumns.Count);
                for (var i = 0; i < count; i++)
                {
                    // DEFAULT never carries a source value of its own to compare - the column's
                    // own DEFAULT constraint (if any) is a DDL-time concern this pass doesn't
                    // re-derive here.
                    if (targetColumns[i]?.Type is not { } targetType || columnValues[i] is DefaultLiteral)
                    {
                        continue;
                    }

                    var sourceExpression = columnValues[i];
                    var sourceType = OperandType(ResolveOperand(sourceExpression, scopeChain));
                    EmitWriteLossFinding(targetTableQualifiedName, targetColumns[i]!.Name, targetType, sourceType, sourceExpression);
                }
            }
        }

        private void AnalyzeSelectListWriteLoss(IList<SelectElement> selectElements, IReadOnlyList<CatalogColumn?> targetColumns, string targetTableQualifiedName)
        {
            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            var count = Math.Min(selectElements.Count, targetColumns.Count);
            for (var i = 0; i < count; i++)
            {
                // Only the InsertSource's own QuerySpecification reaches this method (the WITH
                // clause's CTE bodies are walked separately, before the pending fields are ever
                // set), and AnalyzeInsertWriteLoss only stashes those fields when every select
                // element is a SelectScalarExpression, so this cast is safe.
                var sourceExpression = ((SelectScalarExpression)selectElements[i]).Expression;
                if (targetColumns[i]?.Type is not { } targetType)
                {
                    continue;
                }

                var sourceType = OperandType(ResolveOperand(sourceExpression, scopeChain));
                EmitWriteLossFinding(targetTableQualifiedName, targetColumns[i]!.Name, targetType, sourceType, sourceExpression);
            }
        }

        private void EmitWriteLossFinding(string tableQualifiedName, string columnName, SqlType targetType, SqlType? sourceType, ScalarExpression sourceExpression)
        {
            var kind = Rules.WriteLossClassifier.Classify(targetType, sourceType, sourceExpression);
            if (kind is null)
            {
                return;
            }

            WriteLossFindings.Add(new WriteLossFinding(
                tableQualifiedName, columnName, kind.Value, targetType, sourceType!,
                sourcePath, sourceExpression.StartLine, sourceExpression.StartColumn));
        }

        /// <summary>
        /// Local declarations and parameters don't cross a proc/function boundary, so every
        /// body - however it was introduced - starts with a clean slate. Options (WITH RECOMPILE/
        /// ENCRYPTION/...) exists on ProcedureStatementBody (a procedure), not the shared
        /// ProcedureStatementBodyBase this hook also fires for functions/triggers through - a
        /// scalar/table function can never carry WITH RECOMPILE at all, so the check is naturally
        /// false for those, no special-casing needed beyond the type pattern itself.
        /// </summary>
        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
            RecordParameters(node.Parameters);

            _previousProcedureHasWithRecompile = _procedureHasWithRecompile;
            _procedureHasWithRecompile = node is ProcedureStatementBody { Options: { } options }
                && options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile);
        }

        protected override void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node) =>
            _procedureHasWithRecompile = _previousProcedureHasWithRecompile;

        private bool _previousProcedureHasWithRecompile;

        protected override void OnEnterTriggerBody(TriggerStatementBody node)
        {
            _variables.Clear();
            _formalParameterNames.Clear();
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            var operatorText = ToOperatorText(node.ComparisonType);
            if (operatorText is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison operator", $"unrecognized comparison operator '{node.ComparisonType}'");
                return;
            }

            TryAddFinding(node.FirstExpression, node.SecondExpression, _negated ? Negate(operatorText) : operatorText, node);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            if (node.TernaryExpressionType is not (BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween))
            {
                return;
            }

            // Roadmap Phase E2: NOT BETWEEN was previously treated identically to BETWEEN,
            // reporting `>= lower` / `<= upper` findings for what the engine actually evaluates
            // as `< lower OR > upper` - a WRONG verdict (same failure class the NOT-polarity fix
            // above addressed), not a missing one. Oracle-verified directly: `Col NOT BETWEEN
            // 'a' AND N'z'` produces an Index Scan with BOTH comparisons OR'd together, even
            // when both bounds already match the column's own type - non-sargable regardless of
            // type match, the identical pattern <>/NOT LIKE/NOT IN/<> ALL already follow. A NOT-
            // wrapped ordinary BETWEEN is the same predicate under different syntax, so _negated
            // routes through the same path (this was deliberately deferred when the NOT-polarity
            // fix landed, pending this oracle probe).
            var isNotBetween = node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween || _negated;
            if (isNotBetween)
            {
                if (ScopeStack.Count > 0 && _position == PredicatePosition.Seekable)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT BETWEEN is not sargable regardless of type match - not attributed to a type-conversion verdict");
                }
                else if (_position == PredicatePosition.SuppressedOperand)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
                }

                return;
            }

            // BETWEEN decomposes into `col >= lower AND col <= upper` - both bounds are
            // independent comparisons against the same column and either one alone can force
            // the conversion (docs/audit-remediation-plan.md Phase 4.3), e.g. `Col BETWEEN 1
            // AND N'x'` where only the upper bound carries the higher-precedence literal.
            // Reporting only the lower bound (as this used to) silently dropped that case.
            TryAddFinding(node.FirstExpression, node.SecondExpression, ">=", node);
            TryAddFinding(node.FirstExpression, node.ThirdExpression, "<=", node);
        }

        public override void Visit(LikePredicate node)
        {
            // `NOT (Col LIKE @p)` reaches here with node.NotDefined still false - the NOT sits
            // on the wrapping BooleanNotExpression, not this node - so _negated stands in for it
            // (Roadmap Phase E2, same bug class as the comparison-operator fix above).
            if (node.NotDefined || _negated)
            {
                // NOT LIKE is not sargable regardless of type match - oracle-verified directly
                // (a varchar column compared against a matching-type, non-leading-wildcard
                // pattern still produces an Index Scan for NOT LIKE, where the equivalent LIKE
                // seeks). Attributing that scan to a type-precedence verdict would blame the
                // wrong cause: fixing the type mismatch would not make this predicate seek. Only
                // recorded when this would otherwise have been a candidate (real scope, real
                // filter context) - mirrors every other "not eligible for a verdict" skip below.
                if (ScopeStack.Count > 0 && _position == PredicatePosition.Seekable)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT LIKE is not sargable regardless of type match - not attributed to a type-conversion verdict");
                }
                else if (_position == PredicatePosition.SuppressedOperand)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, OperandPositionConstructKind, OperandPositionLedgerReason);
                }

                return;
            }

            // `varcharCol LIKE @nvarcharPattern` converts the column exactly like `=` does -
            // one of the most common real-world instances of this bug class (ORM-generated N''
            // patterns compared against a non-unicode column), and previously invisible to the
            // verdict engine entirely: Tier-1's LikePredicate visitor only inspects the
            // pattern's wildcard shape, never the column's or pattern's TYPE. Routes through the
            // same TryAddFinding machinery as every other comparison operator - direction,
            // both-column handling, and ledgering all apply identically.
            TryAddFinding(node.FirstExpression, node.SecondExpression, "LIKE", node);
        }

        public override void Visit(InPredicate node)
        {
            if (ScopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            // Inside a query, just not in a filtering position (a SELECT-list CASE branch, an
            // ORDER BY expression) - no seek to lose, not a predicate at all, so this is excluded
            // silently exactly like Tier-1 already excludes it, not ledgered. Unless an enclosing
            // filter clause's own active context was suspended to get here (this IN is itself a
            // CASE/IIF/COALESCE/NULLIF branch) - see EnterOperandPosition/SkipIfNotSeekable.
            if (SkipIfNotSeekable(node))
            {
                return;
            }

            if (node.NotDefined || _negated)
            {
                // NOT IN is not sargable regardless of type match - oracle-verified directly
                // (a varchar column compared against a matching-type NOT IN list still produces
                // an Index Scan, where the equivalent IN seeks). Same reasoning as NOT LIKE
                // above: the type-conversion verdict machinery does not apply here.
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT IN is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            _currentPredicateFragment = node;
            if (ResolveOperand(node.Expression, scopeChain) is not PredicateOperand.Column column)
            {
                // The tested expression isn't a real column (an expression, a CAST result,
                // etc.) - nothing to classify against an index.
                return;
            }

            var otherType = node.Subquery is not null
                ? ResolveInSubqueryType(node.Subquery)
                : CombineListElementTypes(node.Values, scopeChain);

            if (otherType is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "IN predicate", "list contains a non-literal/unresolvable element, or the subquery's output column type could not be resolved");
                return;
            }

            var verdict = VerdictClassifier.Classify(column.Type, otherType, operatorText: "IN");
            Findings.Add(new TypedPredicateFinding(verdict, column, new PredicateOperand.Value(otherType), "IN", sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// Roadmap Phase E2: `col = ANY/SOME (subquery)` and `col &lt;&gt; ALL (subquery)` -
        /// oracle-verified to produce the IDENTICAL CONVERT_IMPLICIT signature as the equivalent
        /// `IN`/`NOT IN` form respectively (probed directly: `Code = ANY (SELECT Code FROM U)`
        /// and `Code IN (SELECT Code FROM U)` both show CONVERT_IMPLICIT(nvarchar(20), Code, 0)
        /// on the identical column), so those two shapes route through the exact same machinery.
        /// Every other operator+quantifier combination (`&gt; ANY`, `&lt; ALL`, etc.) is a range-
        /// type comparison against a whole result set with materially different plan shapes this
        /// pass has not characterized - ledgered rather than guessed. ANY/ALL/SOME under a NOT
        /// wrapper is likewise not modeled (a fifth negation combination this pass has not
        /// oracle-verified) rather than assumed to follow the same pattern as the simpler cases.
        /// </summary>
        public override void Visit(SubqueryComparisonPredicate node)
        {
            if (ScopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (SkipIfNotSeekable(node))
            {
                return;
            }

            var isAnyEquals = node.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.Any && node.ComparisonType == BooleanComparisonType.Equals;
            var isAllNotEquals = node.SubqueryComparisonPredicateType == SubqueryComparisonPredicateType.All
                && node.ComparisonType is BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation;

            if (_negated || (!isAnyEquals && !isAllNotEquals))
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "subquery comparison predicate", $"'{node.ComparisonType} {node.SubqueryComparisonPredicateType}' is not modeled - only '= ANY/SOME' and '<> ALL' are (the IN/NOT IN equivalents)");
                return;
            }

            if (isAllNotEquals)
            {
                // Same reasoning as NOT IN/<> above: oracle-verified non-sargable regardless of
                // type match, so the type-conversion verdict machinery does not apply.
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "<> ALL is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            _currentPredicateFragment = node;
            if (ResolveOperand(node.Expression, scopeChain) is not PredicateOperand.Column column)
            {
                return;
            }

            var otherType = ResolveInSubqueryType(node.Subquery);
            if (otherType is null)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "subquery comparison predicate", "the subquery's output column type could not be resolved");
                return;
            }

            // Operator string is "IN", not "= ANY" - oracle-verified as the identical shape
            // (same CONVERT_IMPLICIT signature), and CorpusFindingProbeBuilder already knows
            // how to build a probe for "IN" (NormalizeOperatorForProbe treats it as "="); a
            // novel "= ANY" string would need its own probe-shape support this pass has no
            // reason to duplicate for what is, for every purpose downstream of this point, the
            // same finding.
            var verdict = VerdictClassifier.Classify(column.Type, otherType, operatorText: "IN");
            Findings.Add(new TypedPredicateFinding(verdict, column, new PredicateOperand.Value(otherType), "IN", sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// Roadmap Phase E2: IS NULL/IS NOT NULL is its own distinct SQL operation, not a value
        /// comparison - no CONVERT_IMPLICIT is possible when comparing to NULL, so there is
        /// nothing here for the verdict engine to classify. An explicit no-op rather than
        /// relying on the default traversal falling through silently, so this construct is
        /// accounted for (ConstructCoverage.json: Handled) instead of having zero trace - the
        /// wrapped expression is a bare column/expression reference with no top-level visitor of
        /// its own in this class regardless (columns are only ever inspected via ResolveOperand,
        /// called directly from a comparison), so whether the base ExplicitVisit still walks
        /// into it afterward has no effect on what this pass finds.
        /// </summary>
        public override void Visit(BooleanIsNullExpression node)
        {
            // Intentionally empty - see the doc comment above.
        }

        private static string? ToOperatorText(BooleanComparisonType comparisonType) => comparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.NotGreaterThan => "!>",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.NotLessThan => "!<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets => "<>",
            BooleanComparisonType.NotEqualToExclamation => "<>",
            _ => null,
        };

        /// <summary>T-SQL's own operator negation, applied when a NOT wraps this comparison - the SAME direction VerdictClassifier already treats <c>&lt;&gt;</c> as non-sargable regardless of type match, so negating <c>=</c> into <c>&lt;&gt;</c> correctly routes to that existing ledgered-skip path rather than reporting the wrong comparison's verdict.</summary>
        private static string Negate(string operatorText) => operatorText switch
        {
            "=" => "<>",
            "<>" => "=",
            ">" => "<=",
            "<" => ">=",
            ">=" => "<",
            "<=" => ">",
            "!<" => "<",
            "!>" => ">",
            _ => operatorText,
        };

        private void RecordParameters(IList<ProcedureParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                _variables[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
                _formalParameterNames.Add(parameter.VariableName.Value);
            }
        }

        private void TryAddFinding(ScalarExpression first, ScalarExpression second, string operatorText, TSqlFragment node)
        {
            if (ScopeStack.Count == 0)
            {
                // A comparison genuinely outside any FROM scope - a bare IF @x = 1/WHILE
                // condition, or any other scalar check with no query underneath it. UPDATE/
                // DELETE/MERGE's own WHERE/ON already push a scope (see ExplicitVisit overrides
                // above), so this is no longer the "second case" the comment here used to
                // describe as a coverage gap - verified against the real corpus (every instance
                // sampled is a bare IF check with no query at all). Recorded rather than
                // silently dropped, since a future regression in that scope-pushing would
                // otherwise degrade into this same bucket unnoticed.
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            // Inside a query, just not in a filtering position (a SELECT-list CASE branch, an
            // ORDER BY expression) - no seek to lose, not a predicate at all, so this is excluded
            // silently exactly like Tier-1 already excludes it, not ledgered. Unless an enclosing
            // filter clause's own active context was suspended to get here (this comparison is
            // itself a CASE/IIF/COALESCE/NULLIF branch) - see EnterOperandPosition/SkipIfNotSeekable.
            if (SkipIfNotSeekable(node))
            {
                return;
            }

            if (operatorText == "<>")
            {
                // <> is not sargable regardless of type match - oracle-verified directly (a
                // varchar column compared against a matching-type value via <> still produces
                // an Index Scan, where = seeks; an indexed int column's <> CAN sometimes split
                // into a two-range seek, but that's an optimizer choice this pass has no basis
                // to promise either way). Attributing that scan to a type-precedence verdict
                // would blame the wrong cause: fixing the type mismatch would not make this
                // predicate seek. !< and !> are NOT included here - T-SQL folds them to >= and
                // <= respectively (oracle-verified), which seek exactly like any other range
                // comparison, so ToOperatorText below still routes them through normally.
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "<> is not sargable regardless of type match - not attributed to a type-conversion verdict");
                return;
            }

            // Innermost scope first, then progressively outer ones - a correlated subquery's
            // predicate can legitimately reference an enclosing query's alias
            // (docs/audit-remediation-plan.md Phase 2.2).
            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            _currentPredicateFragment = node;
            var left = ResolveOperand(first, scopeChain);
            var right = ResolveOperand(second, scopeChain);

            if (left is PredicateOperand.Column leftColumn && right is PredicateOperand.Column rightColumn)
            {
                // Two real columns, same string category, genuinely different resolved
                // collations: this doesn't compile at all (Msg 468, oracle-verified) - not a
                // sargability verdict for either direction, so report the conflict once and
                // skip the normal AddFinding calls rather than also emitting a routine Unknown
                // that would understate what's actually wrong here.
                if (TryRecordCollationConflict(leftColumn, rightColumn, operatorText, node))
                {
                    return;
                }

                // `ON a.x = b.y`: classifying only one side (as this used to, always picking
                // the left operand) misses whichever column is actually on the LOWER-precedence
                // side - a join predicate implicitly converting the right-hand join key is one
                // of the most common real-world instances of this bug class, and the old code
                // silently reported it as SeekPreserved by construction, never even looking at
                // the right column's own verdict. Classify and report BOTH directions; a column
                // that doesn't convert reports SeekPreserved for itself exactly as it should.
                AddFinding(leftColumn, rightColumn, operatorText, node);
                AddFinding(rightColumn, leftColumn, operatorText, node);
                return;
            }

            PredicateOperand.Column? column;
            PredicateOperand? other;
            if (left is PredicateOperand.Column singleLeftColumn)
            {
                (column, other) = (singleLeftColumn, right);
            }
            else if (right is PredicateOperand.Column singleRightColumn)
            {
                (column, other) = (singleRightColumn, left);
            }
            else
            {
                (column, other) = (null, null);
            }

            if (column is null || other is null)
            {
                RecordNoColumnOperand(first, second, node);
                return;
            }

            AddFinding(column, other, operatorText, node);
        }

        /// <summary>
        /// Neither side resolved to a real column. Two genuinely different shapes land here: a
        /// comparison where neither side was ever a bare column reference to begin with (most
        /// commonly a column WRAPPED in COALESCE/CASE/NULLIF/IIF, which resolves through
        /// ResolveOperand's ExpressionTypeInferencer branch into a typed Value rather than a
        /// Column - Tier-1 separately flags this same shape as a syntactic FunctionWrappedColumn
        /// finding when it sits directly in a filter position) is genuinely benign; a bare
        /// ColumnReferenceExpression that failed to resolve in scope (an unresolvable FROM-scope
        /// alias, most commonly) is a real analysis gap that happened to land in the same code
        /// path - ledgered under a distinct ConstructKind so the two are never conflated in the
        /// published honesty numbers.
        /// </summary>
        private void RecordNoColumnOperand(ScalarExpression first, ScalarExpression second, TSqlFragment node)
        {
            if (first is ColumnReferenceExpression || second is ColumnReferenceExpression)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, UnresolvedColumnComparisonConstructKind,
                    "at least one side of this comparison is a bare column reference that failed to resolve to a real column (most commonly an unresolved FROM-scope alias) - not the benign no-column-operand shape");
                return;
            }

            ledger.Record(
                AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NoColumnOperandConstructKind,
                "neither side of this comparison resolved to a real column - most commonly both sides are expressions (e.g. a column wrapped in COALESCE/CASE/NULLIF/IIF compared to a literal)");
        }

        private void AddFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            var otherIsLiteral = other is PredicateOperand.Value { IsLiteral: true };
            var otherType = other is PredicateOperand.Value value ? value.Type : ((PredicateOperand.Column)other).Type;
            var (verdict, unknownReason) = VerdictClassifier.ClassifyWithReason(column.Type, otherType, otherIsLiteral, operatorText);

            Findings.Add(new TypedPredicateFinding(
                verdict, column, other, operatorText, sourcePath, node.StartLine, node.StartColumn,
                UnknownReason: unknownReason,
                PredicateFragmentText: _currentPredicateFragment is { } fragment ? Rules.FragmentTextRenderer.Render(fragment) : null,
                Fingerprint: TypedPredicateFindingIdentity.ComputeFingerprint(column, other, operatorText)));

            TryAddOversizedParameterFinding(column, other, otherIsLiteral, node);
            TryAddUnderLengthParameterFinding(column, other, otherIsLiteral, operatorText, node);
            TryAddAnsiPaddingMismatchFinding(column, other, operatorText, node);
            TryAddLocalVariablePredicateFinding(column, other, operatorText, node);
            TryAddFilteredIndexParameterMismatchFinding(column, other, operatorText, node);
        }

        /// <summary>
        /// docs/detection-checklist.md Tier 2 "Local-variable predicates" - see <see
        /// cref="LocalVariablePredicateFinding"/>'s own doc comment for the full reasoning,
        /// including why an active RECOMPILE guard fully suppresses this rather than merely
        /// downgrading it.
        /// </summary>
        private void TryAddLocalVariablePredicateFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (HasActiveRecompileGuard || other is not PredicateOperand.Value { VariableName: { } variableName, IsFormalParameter: false })
            {
                return;
            }

            LocalVariablePredicateFindings.Add(new LocalVariablePredicateFinding(
                column.TableQualifiedName, column.ColumnName, column.Indexed, column.Depth,
                variableName, operatorText, sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// docs/detection-checklist.md full-archive practitioner sweep §E, "Filtered index whose
        /// predicate compares against a variable/parameter, not a literal" - see
        /// <see cref="FilteredIndexParameterMismatchFinding"/>'s own doc comment for the full
        /// oracle-confirmed reasoning, including why this is deliberately NEVER gated on an active
        /// RECOMPILE guard, unlike <see cref="TryAddLocalVariablePredicateFinding"/> right above.
        /// Fires for BOTH a formal parameter and a plain <c>DECLARE</c>d local variable - unlike
        /// <see cref="LocalVariablePredicateFinding"/>, which deliberately excludes a formal
        /// parameter (its own claim is specific to a local's estimator-invisibility), the filtered-
        /// index match rule this finding reports treats every non-literal operand identically: the
        /// optimizer's own filtered-index-matching rule only ever accepts a literal, full stop,
        /// regardless of whether the non-literal operand is a sniffed parameter or a plain local.
        /// </summary>
        private void TryAddFilteredIndexParameterMismatchFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (other is not PredicateOperand.Value { VariableName: { } variableName, IsFormalParameter: var isFormalParameter }
                || !LiteralEqualityFilteredIndexesByColumn.TryGetValue((column.TableQualifiedName, column.ColumnName), out var candidates))
            {
                return;
            }

            foreach (var (indexName, literalText) in candidates)
            {
                FilteredIndexParameterMismatchFindings.Add(new FilteredIndexParameterMismatchFinding(
                    column.TableQualifiedName, column.ColumnName, indexName, literalText,
                    variableName, isFormalParameter, operatorText, sourcePath, node.StartLine, node.StartColumn));
            }
        }

        /// <summary>
        /// docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #2 - a
        /// parameter/variable/expression declared with a meaningfully LONGER length than the
        /// column it's compared against, within the same string category (a category MISMATCH is
        /// a different, already-covered concern; MAX-typed is its own item #1, not this one - a
        /// declared length of -1 there would falsely read as "shorter", so MAX-typed operands are
        /// excluded here explicitly). A literal's own length is its actual content length, not a
        /// "declared" one, so literals are excluded - only a real variable/parameter/expression
        /// carries a DECLARED length independent of its current value.
        /// </summary>
        private void TryAddOversizedParameterFinding(PredicateOperand.Column column, PredicateOperand other, bool otherIsLiteral, TSqlFragment node)
        {
            if (otherIsLiteral || other is not PredicateOperand.Value { Type: { } otherType })
            {
                return;
            }

            if (column.Type is not { IsStringFamily: true, IsMax: false, Length: { } columnLength } columnType
                || otherType is not { IsStringFamily: true, IsMax: false, Length: { } otherLength }
                || columnType.Category != otherType.Category
                || otherLength <= columnLength)
            {
                return;
            }

            OversizedParameterFindings.Add(new OversizedParameterFinding(
                column.TableQualifiedName, column.ColumnName, columnLength, otherLength, sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// docs/detection-checklist.md Tier 1 "Under-length and length-defaulted string
        /// declarations" - the mirror of <see cref="TryAddOversizedParameterFinding"/>: a
        /// parameter/variable/expression declared with a meaningfully SHORTER length than the
        /// column it's compared against (or no explicit length at all, T-SQL's own length-1
        /// default), within the same string category. Same literal/MAX/category-mismatch
        /// exclusions as the oversized case - a literal's length is its actual content, not a
        /// declared one; MAX-typed is item #1's own separate finding; a category mismatch is
        /// already covered elsewhere.
        /// </summary>
        private void TryAddUnderLengthParameterFinding(
            PredicateOperand.Column column, PredicateOperand other, bool otherIsLiteral, string operatorText, TSqlFragment node)
        {
            if (otherIsLiteral || other is not PredicateOperand.Value { Type: { } otherType })
            {
                return;
            }

            if (column.Type is not { IsStringFamily: true, IsMax: false, Length: { } columnLength }
                || otherType is not { IsStringFamily: true, IsMax: false })
            {
                return;
            }

            if (column.Type.Category != otherType.Category)
            {
                return;
            }

            var isImplicitDefault = otherType.Length is null;
            if (!isImplicitDefault && otherType.Length >= columnLength)
            {
                return;
            }

            var changesRangeOrPatternShape = operatorText is "LIKE" or "<" or "<=" or ">" or ">=";

            UnderLengthParameterFindings.Add(new UnderLengthParameterFinding(
                column.TableQualifiedName, column.ColumnName, columnLength, otherType.Length, isImplicitDefault,
                operatorText, changesRangeOrPatternShape, sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" -
        /// "ANSI_PADDING OFF as a second, independent finding". Scoped to LIKE-against-a-literal
        /// only, narrower than the checklist's original "column vs column, or column vs literal"
        /// framing: oracle-probed directly (real seeded rows) that a plain equality comparison is
        /// NOT affected by ANSI_PADDING regardless of trailing whitespace on either side - T-SQL's
        /// own comparison semantics trim trailing spaces for `=` either way. Only LIKE, where a
        /// pattern's own trailing whitespace is semantically significant (never trimmed), shows a
        /// real difference: a non-padded column can never STORE a value ending in whitespace at
        /// all (stripped at INSERT time), so a pattern with significant trailing whitespace can
        /// never match anything the column could ever contain.
        /// </summary>
        private void TryAddAnsiPaddingMismatchFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            if (operatorText != "LIKE"
                || column.Type is not { Category: SqlTypeCategory.VarChar or SqlTypeCategory.VarBinary }
                || other is not PredicateOperand.Value { IsLiteral: true, LiteralText: { } literalText }
                || !LiteralEndsWithSignificantWhitespace(literalText))
            {
                return;
            }

            var catalogColumn = catalog.Find(column.TableQualifiedName, CurrentProcScope)?.FindColumn(column.ColumnName);
            if (catalogColumn is not { IsAnsiPadded: false })
            {
                return;
            }

            AnsiPaddingMismatchFindings.Add(new AnsiPaddingMismatchFinding(
                column.TableQualifiedName, column.ColumnName, literalText, sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// The literal's own content is whatever sits between its FIRST and LAST single-quote
        /// character - safe regardless of an N-prefix or a trailing ` COLLATE x` suffix, both of
        /// which <see cref="Rules.LiteralTextRenderer"/> can add outside that quoted span.
        /// </summary>
        private static bool LiteralEndsWithSignificantWhitespace(string literalText)
        {
            var firstQuote = literalText.IndexOf('\'');
            var lastQuote = literalText.LastIndexOf('\'');
            if (firstQuote < 0 || lastQuote <= firstQuote)
            {
                return false;
            }

            var content = literalText[(firstQuote + 1)..lastQuote];
            return content.Length > 0 && char.IsWhiteSpace(content[^1]);
        }

        /// <summary>
        /// True (and a <see cref="CollationConflictFinding"/> recorded) when two real columns
        /// are both string-family with genuinely different, both-resolved collations - the one
        /// shape oracle-verified to not compile at all (Msg 468). This is a collation-label
        /// mismatch, not a type-category one: probed directly, `CHAR` vs `VARCHAR` (different
        /// category) with differing collations raises the identical Msg 468 as `VARCHAR` vs
        /// `VARCHAR` with differing collations, and matching collations compile fine regardless
        /// of category (char vs varchar, same collation, joins cleanly). Checking category
        /// equality here (as an earlier version of this method did) let a genuine cross-category
        /// collation conflict fall through to the type-pair matrix and get reported
        /// SeekPreserved for a predicate that does not compile at all - worse than an Unknown.
        /// Neither side can have gone through a self-differing explicit COLLATE here: that's
        /// diverted to an <see cref="ExpressionDerivedFinding"/> before either operand ever
        /// becomes a <see cref="PredicateOperand.Column"/> (<see cref="Lineage.ScalarExpressionResolver"/>'s
        /// ApplyExplicitCollate runs earlier, in Pass 2/3's shared column resolution).
        /// </summary>
        private bool TryRecordCollationConflict(PredicateOperand.Column first, PredicateOperand.Column second, string operatorText, TSqlFragment node)
        {
            if (first.Type is not { IsStringFamily: true, Collation: { } firstCollation }
                || second.Type is not { IsStringFamily: true, Collation: { } secondCollation }
                || string.Equals(firstCollation.Name, secondCollation.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            CollationConflictFindings.Add(new CollationConflictFinding(
                first.TableQualifiedName, first.ColumnName, firstCollation.Name,
                second.TableQualifiedName, second.ColumnName, secondCollation.Name,
                operatorText, sourcePath, node.StartLine, node.StartColumn));
            return true;
        }

        private PredicateOperand ResolveOperand(
            ScalarExpression expression, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            switch (expression)
            {
                case ColumnReferenceExpression columnRef:
                    return ResolveColumnOperand(columnRef, scopeChain);

                case VariableReference variableRef:
                    return new PredicateOperand.Value(
                        _variables.GetValueOrDefault(variableRef.Name), VariableName: variableRef.Name,
                        IsFormalParameter: _formalParameterNames.Contains(variableRef.Name));

                case Literal literal:
                    return new PredicateOperand.Value(Rules.LiteralTypeResolver.Resolve(literal), IsLiteral: true, Rules.LiteralTextRenderer.Render(literal));

                case GlobalVariableExpression globalVariable:
                    return ResolveGlobalVariableOperand(globalVariable);

                case FunctionCall functionCall:
                    return ResolveFunctionCallOperand(functionCall, scopeChain);

                case CastCall castCall:
                    return ResolveCastOrConvertOperand(castCall.DataType, castCall.Parameter, scopeChain, castCall);

                case ConvertCall convertCall:
                    return ResolveCastOrConvertOperand(convertCall.DataType, convertCall.Parameter, scopeChain, convertCall);

                // `col = (SELECT x FROM ...)` - a scalar subquery used as a plain comparison
                // operand, not the `IN`/`= ANY`/`<> ALL` shape ResolveInSubqueryType's two other
                // callers handle. Same machinery: resolve the subquery's single output column
                // through lineage rather than leaving it opaque. A multi-column or otherwise
                // unresolvable subquery still resolves Unknown, exactly as before - never a guess.
                case ScalarSubquery scalarSubquery:
                    return new PredicateOperand.Value(ResolveInSubqueryType(scalarSubquery));

                // Roadmap Phase B: arithmetic, CASE/COALESCE/NULLIF/IIF - CLAUDE.md's own named
                // hard cases - now resolved through the shared ExpressionTypeInferencer instead
                // of falling to Unknown. The leaf callback recurses back into ResolveOperand for
                // any column/variable/function-call reachable inside a branch, so e.g.
                // `CASE WHEN x THEN SomeColumn ELSE @p END` types SomeColumn through the exact
                // same scope-aware path a bare column operand would.
                case ParenthesisExpression or UnaryExpression or BinaryExpression
                    or CoalesceExpression or NullIfExpression or IIfCall
                    or SearchedCaseExpression or SimpleCaseExpression:
                    return new PredicateOperand.Value(
                        ExpressionTypeInferencer.Resolve(expression, e => OperandType(ResolveOperand(e, scopeChain)), catalog.TypeAliases));

                default:
                    // Most commonly a scalar UDF (no return-type registry - only built-in
                    // functions are curated), but also any other scalar expression kind this
                    // pass doesn't type (e.g. a scalar subquery). The operand still resolves
                    // Unknown, exactly as before - this only makes it counted instead of
                    // silently falling through.
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, expression.StartLine, expression.StartColumn,
                        PredicateOperandConstructKind, $"operand of kind '{expression.GetType().Name}' has no type resolution - resolved Unknown");
                    return new PredicateOperand.Value(Type: null);
            }
        }

        /// <summary>Extracts the resolved type out of either <see cref="PredicateOperand"/> shape - shared by every ExpressionTypeInferencer leaf callback in this pass.</summary>
        private static SqlType? OperandType(PredicateOperand operand) => operand switch
        {
            PredicateOperand.Value v => v.Type,
            PredicateOperand.Column c => c.Type,
            _ => null,
        };

        /// <summary>
        /// <c>@@SPID</c>, <c>@@ROWCOUNT</c>, etc. - typed from the curated, oracle-verified
        /// table (<see cref="Rules.BuiltinFunctionTypeResolver"/>), never guessed.
        /// </summary>
        private PredicateOperand.Value ResolveGlobalVariableOperand(GlobalVariableExpression globalVariable)
        {
            var type = Rules.BuiltinFunctionTypeResolver.ResolveGlobalVariable(globalVariable.Name);
            if (type is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, globalVariable.StartLine, globalVariable.StartColumn,
                    PredicateOperandConstructKind, $"global variable '{globalVariable.Name}' has no type resolution - resolved Unknown");
            }

            return new PredicateOperand.Value(type);
        }

        /// <summary>
        /// A built-in scalar function call - typed from the curated, oracle-verified table
        /// (<see cref="Rules.BuiltinFunctionTypeResolver"/>). ISNULL is the one function in that
        /// table whose return type is its own first argument's type rather than a fixed type
        /// (oracle-verified: ISNULL never applies data type precedence across its arguments the
        /// way COALESCE does), so it recurses into that argument through the same operand
        /// resolution every other expression goes through. A function not in the table - most
        /// commonly a scalar UDF, or a built-in this curated table doesn't cover yet - still
        /// resolves Unknown, never guessed.
        /// </summary>
        private PredicateOperand.Value ResolveFunctionCallOperand(
            FunctionCall functionCall, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var name = functionCall.FunctionName.Value;

            if (Rules.BuiltinFunctionTypeResolver.TryGetArgumentTypeIndex(name) is { } argumentIndex && functionCall.Parameters.Count > argumentIndex)
            {
                var argumentType = OperandType(ResolveOperand(functionCall.Parameters[argumentIndex], scopeChain));
                if (argumentType is not null && Rules.BuiltinFunctionTypeResolver.WidensIntegerAggregateArgument(name))
                {
                    argumentType = Rules.BuiltinFunctionTypeResolver.WidenIntegerAggregateResult(argumentType);
                }
                else if (argumentType is not null && Rules.BuiltinFunctionTypeResolver.RequiresDateAddResultAdjustment(name))
                {
                    argumentType = Rules.BuiltinFunctionTypeResolver.ResolveDateAddResult(argumentType);
                }

                return new PredicateOperand.Value(argumentType);
            }

            var fixedType = Rules.BuiltinFunctionTypeResolver.ResolveFixedReturnType(name);
            if (fixedType is not null)
            {
                return new PredicateOperand.Value(fixedType);
            }

            // Not a built-in - try the scalar UDF return-type registry (CatalogBuilder
            // registers every CREATE/ALTER FUNCTION with a scalar RETURNS clause under its
            // qualified name). A function this scan never saw declared, or one whose RETURNS
            // clause is itself unresolvable, still resolves Unknown - never guessed.
            var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(functionCall);
            if (catalog.TryGetScalarFunctionReturnType(qualifiedName, out var udfType))
            {
                if (udfType is null)
                {
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                        PredicateOperandConstructKind, $"function '{qualifiedName}' RETURNS type could not be resolved - resolved Unknown");
                }

                return new PredicateOperand.Value(udfType);
            }

            ledger.Record(
                AnalysisPass.Predicates, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                PredicateOperandConstructKind, $"function '{name}' has no return-type resolution - resolved Unknown");

            return new PredicateOperand.Value(Type: null);
        }

        /// <summary>
        /// CAST/CONVERT's explicit target type - always knowable, never a guess. Mirrors Pass
        /// 2's identical collation propagation (<see cref="ScalarExpressionResolver"/>): a
        /// CAST/CONVERT to a string-family type has no inline COLLATE syntax of its own, and the
        /// real engine propagates a string INPUT's own collation into the result
        /// (oracle-verified there, not re-derived here) - a non-string input's result collation
        /// stays unresolved (Unknown), not guessed.
        /// </summary>
        private PredicateOperand.Value ResolveCastOrConvertOperand(
            DataTypeReference dataType, ScalarExpression parameter,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            TSqlFragment node)
        {
            // An unsized CAST/CONVERT to a string/binary-family type silently means 30
            // characters (oracle-confirmed - docs/detection-checklist.md "Small precise adds",
            // "Explicit-length audit of CAST/CONVERT to a string type"), not the length-1
            // default a bare DECLARE with no length gets - see SqlTypeReferenceResolver.Resolve's
            // own doc comment. This lets the existing under-length/oversized-parameter
            // comparison logic (TryAddUnderLengthParameterFinding/TryAddOversizedParameterFinding)
            // pick up a genuinely truncating unsized CAST/CONVERT automatically, with no new
            // finding type needed.
            var type = Parsing.SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases, unsizedStringOrBinaryDefaultLength: 30);
            if (type is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                    PredicateOperandConstructKind, "CAST/CONVERT target type could not be resolved - resolved Unknown");
                return new PredicateOperand.Value(Type: null);
            }

            if (type.IsStringFamily)
            {
                var innerType = OperandType(ResolveOperand(parameter, scopeChain));

                if (innerType is { IsStringFamily: true, Collation: { } innerCollation })
                {
                    type = type with { Collation = innerCollation };
                }
            }

            return new PredicateOperand.Value(type);
        }

        // T-SQL applies data type precedence once across the WHOLE IN list, not element by
        // element: a single higher-precedence literal anywhere in the list forces the column to
        // convert for the comparison as a whole, even when every other element matches the
        // column's own type (docs/audit-remediation-plan.md Phase 4.3 - empirically confirmed
        // against the real oracle: `Col IN ('a', N'b', 'c')` converts Col exactly like
        // `Col IN (N'a', N'b', N'c')` does). SqlTypeCategory's declaration order already encodes
        // T-SQL precedence rank (see DataTypePrecedence), so the highest-ranked element's own
        // type stands in for the list as a whole. Any unresolvable element (a sub-expression, an
        // untyped variable) means the true effective type can't be known, so the whole list is
        // never guessed at (CLAUDE.md precision discipline) - returns null, caller records a
        // ledger skip.
        private SqlType? CombineListElementTypes(
            IList<ScalarExpression> values, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            SqlType? best = null;
            foreach (var value in values)
            {
                var type = OperandType(ResolveOperand(value, scopeChain));

                if (type is null)
                {
                    return null;
                }

                if (best is null || type.Category > best.Category)
                {
                    best = type;
                }
            }

            return best;
        }

        // `col IN (SELECT X FROM ...)` - resolves the subquery's single output column through
        // the same lineage machinery a view's SELECT list uses (CLAUDE.md: "resolve the
        // subquery's single output column through lineage"), rather than treating it as opaque.
        private SqlType? ResolveInSubqueryType(ScalarSubquery subquery)
        {
            var innerCtes = CurrentCteRelations();
            var columns = QueryExpressionResolver.Resolve(subquery.QueryExpression, catalog, resolvedViews, sourcePath, ledger, innerCtes, CurrentProcScope);
            if (columns.Count != 1)
            {
                // A genuinely single-output-column subquery is the only shape this pass has a
                // well-defined answer for. `columns[0]` unconditionally here used to type off
                // the wrong column whenever resolution returned more than one (a multi-column
                // subquery, or a mis-shaped resolution reordering columns) with no check or
                // ledger trace at all - both callers already ledger a null result generically
                // ("the subquery's output column type could not be resolved"), so this is
                // Unknown, not a guess, exactly like the zero-column case just below it always was.
                return null;
            }

            return ColumnProvenanceAnalysis.TryGetScalarType(columns[0].Provenance);
        }

        private PredicateOperand ResolveColumnOperand(
            ColumnReferenceExpression columnRef, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            // Same resolver Pass 2 uses (ScalarExpressionResolver) - a qualified reference whose
            // qualifier doesn't resolve anywhere in the chain is unresolved here too, never a
            // name-only fallback search across the whole FROM scope (Phase 2.1), but a reference
            // to an outer query's alias correctly resolves there (Phase 2.2).
            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger);
            var columnName = columnRef.MultiPartIdentifier.Identifiers[^1].Value;

            if (provenance is ColumnProvenance.BaseColumn baseColumn)
            {
                // Scoped lookup (coverage-remediation-plan.md Phase 3.2, found while wiring up
                // table-valued parameters): a #temp table or table variable is cataloged under a
                // key scoped to its enclosing procedure/function/trigger, but BaseColumn carries
                // only the bare qualified name, so an unscoped Find here silently missed the
                // catalog entry and always reported Indexed=false for any indexed temp
                // object - a real table was never stored with a scope, so passing one is always
                // safe (DatabaseCatalog falls back to the unscoped lookup automatically).
                var matchedIndex = catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.FindIndexedColumn(baseColumn.ColumnName);
                var immediateRelation = ScalarExpressionResolver.TryResolveImmediateRelation(columnRef, scopeChain);
                return new PredicateOperand.Column(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, matchedIndex is not null, baseColumn.Depth, baseColumn,
                    immediateRelation?.RelationQualifiedName, immediateRelation?.ExposedColumnName, matchedIndex?.Name);
            }

            if (provenance is ColumnProvenance.Declared declared)
            {
                // A multi-statement TVF's own RETURNS TABLE(...) column (docs/audit-remediation-
                // plan.md Phase 4.2) - a real, known type, but never traceable to a real catalog
                // table or index (there is none - it's the function's internal table variable).
                return new PredicateOperand.Column(declared.TableQualifiedName ?? "?", columnName, declared.Type, Indexed: false, declared.Depth, declared);
            }

            if (ColumnProvenanceAnalysis.IsExpressionDerived(provenance))
            {
                RecordExpressionDerivedFinding(columnName, columnRef, provenance, scopeChain);
            }
            else if (provenance is ColumnProvenance.Union union)
            {
                // A UNION-view column (the common partitioned-view pattern) usually can't get a
                // single verdict - branches can resolve to entirely different base columns - but
                // when EVERY branch independently agrees on type (and, for the string family,
                // collation - AllBranchesAgree, reused from Pass 2's own TryGetScalarType so this
                // can never drift from what Pass 2 already proved), the comparison's OWN type
                // behavior is fully determined regardless of which branch actually produced the
                // row: T-SQL doesn't narrow a UNION's output type per-row, so one agreed type
                // is not a guess, it's the column's real, single runtime type. No single branch's
                // TableQualifiedName is picked (that WOULD be a guess when branches differ), so
                // this stays Indexed=false and reports "?" the same way a Declared column (also
                // not traceable to one real catalog table) already does - it can still classify
                // ScanForced/SeekPreserved/RangeSeek, just never "this seeks via an index".
                var agreedType = ColumnProvenanceAnalysis.TryGetScalarType(union);
                if (agreedType is not null)
                {
                    return new PredicateOperand.Column("?", columnName, agreedType, Indexed: false, Depth: 0, union);
                }

                // Branches disagree (or one of them is itself unresolved) - genuinely not
                // eligible for a verdict. Ledgering this (unlike the Cast/Expression case above,
                // whose non-eligibility is already reported as its own ExpressionDerivedFinding)
                // closes a silent-clean gap: before this, a predicate against a union-backed
                // column produced no finding AND no ledger entry at all, contradicting the
                // "never silently counted as clean" contract every other unresolvable path in
                // this file honors.
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                    PredicateOperandConstructKind, $"column '{columnName}' resolves through a UNION view whose branches disagree on type - not eligible for a single verdict, never guessed");
            }

            // Cast/Expression (reported above)/Union (reported above)/Unknown/Declared - not
            // eligible for the type-precedence "indexed column" side of a verdict.
            return new PredicateOperand.Value(Type: null);
        }

        private void RecordExpressionDerivedFinding(
            string columnName, ColumnReferenceExpression columnRef, ColumnProvenance provenance,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var underlyingBaseColumns = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(provenance)
                .Select(bc => new UnderlyingBaseColumn(bc.TableQualifiedName, bc.ColumnName, catalog.Find(bc.TableQualifiedName, CurrentProcScope)?.IsIndexedColumn(bc.ColumnName) ?? false))
                .ToList();

            if (underlyingBaseColumns.Count == 0)
            {
                // No traceable base column underneath (e.g. ROW_NUMBER(), a derived-table
                // alias over another expression, an XML .value() shred) - true that it's
                // expression-derived, but nothing actionable to point at, so no
                // ExpressionDerivedFinding is reported. Still ledgered rather than silently
                // dropped, so this deliberate decision is countable like every other "nothing to
                // classify here" branch in this pass.
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                    "expression-derived predicate", $"'{columnName}' is expression-derived but no underlying base column could be traced (e.g. ROW_NUMBER(), a derived-table alias over another expression, an XML shred) - nothing actionable to report");
                return;
            }

            var transformationChain = ColumnProvenanceAnalysis.DescribeTransformationChain(provenance);

            // Roadmap Phase E3: TryResolveImmediateRelation only returns non-null for a real,
            // catalog-known view/TVF layer (ScalarExpressionResolver's own IsViewLayer check) -
            // an inline derived table/CTE in the same statement isn't an independently queryable
            // object a probe could target on its own, so PredicateFragmentText is still captured
            // (harmless on its own) but ImmediateRelationQualifiedName stays null, and
            // ExpressionDerivedProbeBuilder treats that as not-probeable rather than guessing.
            var immediateRelation = ScalarExpressionResolver.TryResolveImmediateRelation(columnRef, scopeChain);
            var identifiers = columnRef.MultiPartIdentifier.Identifiers;
            var alias = identifiers.Count >= 2 ? identifiers[^2].Value : null;

            ExpressionDerivedFindings.Add(new ExpressionDerivedFinding(
                columnName, sourcePath, columnRef.StartLine, columnRef.StartColumn, transformationChain, underlyingBaseColumns,
                PredicateFragmentText: _currentPredicateFragment is { } fragment ? Rules.FragmentTextRenderer.Render(fragment) : null,
                ImmediateRelationQualifiedName: immediateRelation?.RelationQualifiedName,
                ImmediateRelationAlias: immediateRelation is not null ? alias : null));
        }
    }
}
