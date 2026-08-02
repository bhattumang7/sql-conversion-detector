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
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SqlType?>? externalVariables = null, DynamicSqlScope? enclosingScope = null)
    {
        var resolvedViews = lineage.AllRelations;
        var ledger = new SkipLedger();
        var visitor = new Visitor(parseResult.SourcePath, catalog, resolvedViews, externalVariables, ledger, enclosingScope);
        visitor.SeedEnclosingScope(parseResult.Fragment);
        parseResult.Fragment.Accept(visitor);
        return new PredicateExtractionResult(visitor.Findings, visitor.ExpressionDerivedFindings, visitor.CollationConflictFindings, ledger.Entries);
    }

    private sealed class Visitor(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, SqlType?>? externalVariables,
        SkipLedger ledger,
        DynamicSqlScope? enclosingScope = null) : TSqlFragmentVisitor
    {
        /// <summary>Skip-ledger construct kind shared by every "this operand has no type resolution" entry below - one label for the whole family of unresolved-operand reasons.</summary>
        private const string PredicateOperandConstructKind = "predicate operand";

        /// <summary>Skip-ledger construct kind shared by every operator that is oracle-verified non-sargable regardless of type match (&lt;&gt;, NOT LIKE, NOT IN, &lt;&gt; ALL) - the type-conversion verdict machinery never applies to these.</summary>
        private const string NonSeekableOperatorConstructKind = "non-seekable operator";

        private readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> _scopeStack = new();
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();
        private string? _currentProcScope = enclosingScope?.ProcScope;

        /// <summary>Pushes the enclosing trigger's inserted/deleted pseudo-tables onto the CTE stack, if any - called once, before the visitor starts walking, so they're visible for the whole reparsed fragment exactly like a real trigger body's own VisitTriggerBody does.</summary>
        public void SeedEnclosingScope(TSqlFragment rootFragment)
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                _cteStack.Push(BuildTriggerPseudoTableRelations(target, rootFragment));
            }
        }

        // Mirrors NonSargablePredicateScanner's identical tracker (CLAUDE.md Tier-1 scope note:
        // "never a SELECT list, ORDER BY, or GROUP BY - there's no seek to lose"): a comparison
        // that never filters rows isn't a verdict-bearing finding either, e.g. a CASE expression
        // in a SELECT list comparing a column to a literal. Before this, TypedPredicateExtractor
        // had no such gating at all and reported a ScanForced/RangeSeek verdict for ANY
        // comparison anywhere in the tree, filter or not.
        private bool _inFilterContext;

        // Roadmap Phase E2: `WHERE NOT (Col = @p)` previously visited the inner
        // BooleanComparisonExpression completely unaware of the enclosing NOT - the default
        // ExplicitVisit(BooleanNotExpression) recurses into .Expression with no polarity
        // tracking at all, so this reported a plain `=` finding for what the engine actually
        // sees as `<>` (a materially different, oracle-verified-non-sargable comparison,
        // CLAUDE.md's own precision discipline turned against itself: a WRONG verdict, not a
        // missing one). A bool rather than a stack because BooleanNotExpression nests by
        // toggling parity (NOT NOT X == X), never by depth.
        private bool _negated;
        private readonly Dictionary<string, SqlType?> _variables = externalVariables is null
            ? new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SqlType?>(externalVariables, StringComparer.OrdinalIgnoreCase);

        public List<TypedPredicateFinding> Findings { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerivedFindings { get; } = [];

        public List<CollationConflictFinding> CollationConflictFindings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            // A WITH clause's CTEs are visible for the whole statement they're declared in, not
            // scoped per nested QuerySpecification (docs/audit-remediation-plan.md Phase 2.4). A
            // statement with no WITH clause of its own still sees any outer statement's CTEs (a
            // derived-table subquery nested inside a CTE-using statement), so this always pushes
            // something - an unchanged copy of the current top when there's nothing new to add.
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            _cteStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            _scopeStack.Push(FromScopeResolver.Resolve(node.FromClause, catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), _currentProcScope));

            // Reset to false for every part of this query specification except its own WHERE/
            // HAVING (whose own overrides below turn it back on) - without this, an outer
            // WHERE's own nested subquery (an EXISTS/IN (SELECT ...)) would inherit "filter
            // context = true" for that subquery's unrelated SELECT list, and a top-level SELECT
            // list would inherit whatever the enclosing scope happened to be.
            var previousFilterContext = _inFilterContext;
            _inFilterContext = false;

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

            _inFilterContext = previousFilterContext;
            _scopeStack.Pop();
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        /// <summary>A JOIN's ON clause is a filter context exactly like WHERE; the table references it joins are not (a derived-table subquery there has its own SELECT list to protect).</summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _inFilterContext;
            _inFilterContext = true;
            node.SearchCondition?.Accept(this);
            _inFilterContext = previous;
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
            _scopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
            _cteStack.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            _scopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
            _cteStack.Pop();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);

            // A single scope push covers the ON clause and every WHEN [NOT] MATCHED action's
            // own additional condition uniformly, since base.ExplicitVisit walks the whole
            // MergeSpecification subtree with this scope active. MergeSpecification's ON
            // condition (and each action clause's own extra condition) is a raw BooleanExpression,
            // not wrapped in a WhereClause node the way SELECT/UPDATE/DELETE's WHERE is - there is
            // no SELECT-list analog anywhere inside a MergeSpecification for filter-context = true
            // to wrongly leak into, so it's safe to hold it for the whole subtree; any nested
            // subquery still resets it via the QuerySpecification override above.
            _scopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));
            var previousFilterContext = _inFilterContext;
            _inFilterContext = true;
            base.ExplicitVisit(node);
            _inFilterContext = previousFilterContext;
            _scopeStack.Pop();
            _cteStack.Pop();
        }

        // ScriptDOM's visitor double-dispatches through each concrete node type's own Accept()
        // method, which binds at compile time to the most specific ExplicitVisit overload that
        // exists - so overriding only the common ProcedureStatementBodyBase base type would
        // never fire for e.g. an AlterProcedureStatement node. Real-world corpora routinely ship
        // a body-less "CREATE PROCEDURE ... AS RETURN 0" stub followed by the real body via
        // ALTER PROCEDURE (DynamicSqlScanner already had to handle this same pattern for the
        // First Responder Kit corpus repo) - without these overrides, an ALTER PROCEDURE body
        // was walked with the PREVIOUS procedure's stale _variables still in scope, and its own
        // parameters were never recorded at all (docs/audit-remediation-plan.md Phase 2.3). The
        // qualified name each override passes through is also this body's temp-table/table-
        // variable scope key (Phase 2.5), matching CatalogBuilder's identical scoping so a
        // predicate inside a procedure can find that same procedure's own temp objects.
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

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

        private void PushCteScope(WithCtesAndXmlNamespaces? withClause)
        {
            var currentCtes = CurrentCteRelations();
            var ctes = CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, ledger, _currentProcScope);
            _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
        }

        private IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
            _cteStack.Count > 0 ? _cteStack.Peek() : EmptyCteRelations;

        private FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
            new(catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), _currentProcScope);

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

        private static Dictionary<string, ResolvedRelation> MergeCtes(
            IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner)
        {
            // Inner (this statement's own) CTEs take precedence over an outer statement's
            // same-named CTE, matching how an inner scope shadows an outer one everywhere else
            // in this pass.
            var merged = new Dictionary<string, ResolvedRelation>(outer, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, relation) in inner)
            {
                merged[name] = relation;
            }

            return merged;
        }

        private void VisitProcedureOrFunctionBody(ProcedureStatementBodyBase node, SchemaObjectName name)
        {
            // Local declarations and parameters don't cross a proc/function boundary, so every
            // body - however it was introduced - starts with a clean slate.
            _variables.Clear();
            RecordParameters(node.Parameters);

            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentProcScope = previousScope;
        }

        private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject)
        {
            _variables.Clear();

            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);

            // A DDL trigger (ON DATABASE/ON ALL SERVER) or LOGON trigger has no target object -
            // TriggerObject.Name is null whenever TriggerScope isn't Normal (coverage-
            // remediation-plan.md Phase 0.3, reproduced: this used to be an unguarded dereference
            // that took down the whole scan). Neither kind has an inserted/deleted rowset at all (a
            // DDL trigger gets its data from EVENTDATA(), not a pseudo-table), so there is nothing
            // to guess here - record it and still walk the body, since it may still contain
            // ordinary predicates against real tables.
            if (triggerObject.Name is not { } targetTableName)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                    "DDL/LOGON trigger", $"trigger scope '{triggerObject.TriggerScope}' has no target table - no inserted/deleted pseudo-tables to resolve");
                node.AcceptChildren(this);
                _currentProcScope = previousScope;
                return;
            }

            // inserted/deleted are visible throughout the whole trigger body, not just a single
            // top-level SELECT - pushed onto the same CTE stack a real WITH clause uses (they're
            // resolved identically by FromScopeResolver, a named relation checked before the
            // catalog/views), so nested subqueries inherit them the same way a CTE would.
            _cteStack.Push(MergeCtes(CurrentCteRelations(), BuildTriggerPseudoTableRelations(targetTableName, node)));
            node.AcceptChildren(this);
            _cteStack.Pop();

            _currentProcScope = previousScope;
        }

        /// <summary>
        /// inserted/deleted are shaped exactly like the trigger's own target table or view (docs/
        /// audit-remediation-plan.md, trigger inserted/deleted resolution - a gap found auditing
        /// this pass, not on the original remediation plan): a predicate against inserted.Col
        /// reflects that real column's type, but NOT its index - inserted/deleted are a version-
        /// store rowset with no index of their own (coverage-remediation-plan.md Phase 1.1), so
        /// this uses <see cref="FromScopeResolver.ToPseudoTableRelation(Catalog.CatalogTable?, string)"/> rather than the ordinary
        /// FROM-clause conversion, which would wrongly inherit a real index. An INSTEAD OF trigger
        /// can target a VIEW rather than a table (Phase 3.3) - DatabaseCatalog holds no views, so
        /// resolvedViews (the same lookup FromScopeResolver's own NamedTableReference case checks
        /// before falling back to the catalog) is consulted first.
        /// </summary>
        private IReadOnlyDictionary<string, ResolvedRelation> BuildTriggerPseudoTableRelations(SchemaObjectName targetTableName, TSqlFragment node)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(targetTableName);

            ResolvedRelation relation;
            if (resolvedViews.TryGetValue(qualifiedName, out var viewRelation))
            {
                relation = FromScopeResolver.ToPseudoTableRelation(viewRelation, qualifiedName);
            }
            else if (catalog.Find(qualifiedName) is { } table)
            {
                relation = FromScopeResolver.ToPseudoTableRelation(table, qualifiedName);
            }
            else
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                    "trigger inserted/deleted", $"trigger target '{qualifiedName}' has no known DDL and is not a resolved view - inserted/deleted left unresolved");
                return EmptyCteRelations;
            }

            return new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
            {
                ["inserted"] = relation,
                ["deleted"] = relation,
            };
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
            if (node.TernaryExpressionType is BooleanTernaryExpressionType.Between or BooleanTernaryExpressionType.NotBetween)
            {
                // BETWEEN decomposes into `col >= lower AND col <= upper` - both bounds are
                // independent comparisons against the same column and either one alone can
                // force the conversion (docs/audit-remediation-plan.md Phase 4.3), e.g.
                // `Col BETWEEN 1 AND N'x'` where only the upper bound carries the
                // higher-precedence literal. Reporting only the lower bound (as this used to)
                // silently dropped that case.
                TryAddFinding(node.FirstExpression, node.SecondExpression, ">=", node);
                TryAddFinding(node.FirstExpression, node.ThirdExpression, "<=", node);
            }
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
                if (_scopeStack.Count > 0 && _inFilterContext)
                {
                    ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NonSeekableOperatorConstructKind, "NOT LIKE is not sargable regardless of type match - not attributed to a type-conversion verdict");
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
            if (_scopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (!_inFilterContext)
            {
                // Inside a query, just not in a filtering position (a SELECT-list CASE branch,
                // an ORDER BY expression) - no seek to lose, not a predicate at all, so this is
                // excluded silently exactly like Tier-1 already excludes it, not ledgered.
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

            var scopeChain = _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
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

            var verdict = VerdictClassifier.Classify(column.Type, otherType);
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
            if (_scopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            if (!_inFilterContext)
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

            var scopeChain = _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
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
            var verdict = VerdictClassifier.Classify(column.Type, otherType);
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
            }
        }

        private void TryAddFinding(ScalarExpression first, ScalarExpression second, string operatorText, TSqlFragment node)
        {
            if (_scopeStack.Count == 0)
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

            if (!_inFilterContext)
            {
                // Inside a query, just not in a filtering position (a SELECT-list CASE branch,
                // an ORDER BY expression) - no seek to lose, not a predicate at all, so this is
                // excluded silently exactly like Tier-1 already excludes it, not ledgered.
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
            var scopeChain = _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
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
                return;
            }

            AddFinding(column, other, operatorText, node);
        }

        private void AddFinding(PredicateOperand.Column column, PredicateOperand other, string operatorText, TSqlFragment node)
        {
            var otherIsLiteral = other is PredicateOperand.Value { IsLiteral: true };
            var otherType = other is PredicateOperand.Value value ? value.Type : ((PredicateOperand.Column)other).Type;
            var verdict = VerdictClassifier.Classify(column.Type, otherType, otherIsLiteral);

            Findings.Add(new TypedPredicateFinding(verdict, column, other, operatorText, sourcePath, node.StartLine, node.StartColumn));
        }

        /// <summary>
        /// True (and a <see cref="CollationConflictFinding"/> recorded) when two real columns
        /// are the same string category with genuinely different, both-resolved collations -
        /// the one shape oracle-verified to not compile at all (Msg 468). Neither side can have
        /// gone through a self-differing explicit COLLATE here: that's diverted to an
        /// <see cref="ExpressionDerivedFinding"/> before either operand ever becomes a
        /// <see cref="PredicateOperand.Column"/> (<see cref="Lineage.ScalarExpressionResolver"/>'s
        /// ApplyExplicitCollate runs earlier, in Pass 2/3's shared column resolution).
        /// </summary>
        private bool TryRecordCollationConflict(PredicateOperand.Column first, PredicateOperand.Column second, string operatorText, TSqlFragment node)
        {
            if (first.Type is not { IsStringFamily: true, Collation: { } firstCollation } firstType
                || second.Type is not { IsStringFamily: true, Collation: { } secondCollation } secondType
                || firstType.Category != secondType.Category
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
                    return new PredicateOperand.Value(_variables.GetValueOrDefault(variableRef.Name));

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

            if (Rules.BuiltinFunctionTypeResolver.TakesFirstArgumentType(name) && functionCall.Parameters.Count > 0)
            {
                return new PredicateOperand.Value(OperandType(ResolveOperand(functionCall.Parameters[0], scopeChain)));
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
            var qualifiedName = ResolveFunctionQualifiedName(functionCall);
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

        /// <summary>schema.name for a function call target, defaulting to dbo exactly like <see cref="SchemaObjectNameHelper.Resolve"/> does for tables - a function call has no dedicated SchemaObjectName of its own (FunctionName and CallTarget are separate properties), so this rebuilds the same shape by hand.</summary>
        private static string ResolveFunctionQualifiedName(FunctionCall functionCall)
        {
            var schema = functionCall.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: [.., { } last] }
                ? last.Value
                : SchemaObjectNameHelper.DefaultSchema;

            return $"{schema}.{functionCall.FunctionName.Value}";
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
            var type = Parsing.SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases);
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
            var columns = QueryExpressionResolver.Resolve(subquery.QueryExpression, catalog, resolvedViews, sourcePath, ledger, innerCtes, _currentProcScope);
            return columns.Count == 0 ? null : ColumnProvenanceAnalysis.TryGetScalarType(columns[0].Provenance);
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
                var indexed = catalog.Find(baseColumn.TableQualifiedName, _currentProcScope)?.IsIndexedColumn(baseColumn.ColumnName) ?? false;
                var immediateRelation = ScalarExpressionResolver.TryResolveImmediateRelation(columnRef, scopeChain);
                return new PredicateOperand.Column(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, indexed, baseColumn.Depth, baseColumn,
                    immediateRelation?.RelationQualifiedName, immediateRelation?.ExposedColumnName);
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
                RecordExpressionDerivedFinding(columnName, columnRef, provenance);
            }
            else if (provenance is ColumnProvenance.Union)
            {
                // A UNION-view column (the common partitioned-view pattern) is genuinely not
                // eligible for a verdict here - branches can resolve to different base columns
                // entirely, so there is no single "the column" to classify. Ledgering this
                // (unlike the Cast/Expression case above, whose non-eligibility is already
                // reported as its own ExpressionDerivedFinding) closes a silent-clean gap:
                // before this, a predicate against a union-backed column produced no finding
                // AND no ledger entry at all, contradicting the "never silently counted as
                // clean" contract every other unresolvable path in this file honors.
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, columnRef.StartLine, columnRef.StartColumn,
                    PredicateOperandConstructKind, $"column '{columnName}' resolves through a UNION view - branches are not eligible for a single verdict, never guessed");
            }

            // Cast/Expression (reported above)/Union (reported above)/Unknown/Declared - not
            // eligible for the type-precedence "indexed column" side of a verdict.
            return new PredicateOperand.Value(Type: null);
        }

        private void RecordExpressionDerivedFinding(string columnName, ColumnReferenceExpression columnRef, ColumnProvenance provenance)
        {
            var underlyingBaseColumns = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(provenance)
                .Select(bc => new UnderlyingBaseColumn(bc.TableQualifiedName, bc.ColumnName, catalog.Find(bc.TableQualifiedName, _currentProcScope)?.IsIndexedColumn(bc.ColumnName) ?? false))
                .ToList();

            if (underlyingBaseColumns.Count == 0)
            {
                // No traceable base column underneath (e.g. ROW_NUMBER(), a derived-table
                // alias over another expression, an XML .value() shred) - true that it's
                // expression-derived, but nothing actionable to point at, so not reported.
                return;
            }

            var transformationChain = ColumnProvenanceAnalysis.DescribeTransformationChain(provenance);
            ExpressionDerivedFindings.Add(new ExpressionDerivedFinding(
                columnName, sourcePath, columnRef.StartLine, columnRef.StartColumn, transformationChain, underlyingBaseColumns));
        }
    }
}
