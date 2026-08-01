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
    // query text itself. Null/empty for ordinary static SQL.
    public static PredicateExtractionResult Extract(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SqlType?>? externalVariables = null)
    {
        var resolvedViews = lineage.AllRelations;
        var ledger = new SkipLedger();
        var visitor = new Visitor(parseResult.SourcePath, catalog, resolvedViews, externalVariables, ledger);
        parseResult.Fragment.Accept(visitor);
        return new PredicateExtractionResult(visitor.Findings, visitor.ExpressionDerivedFindings, ledger.Entries);
    }

    private sealed class Visitor(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, SqlType?>? externalVariables,
        SkipLedger ledger) : TSqlFragmentVisitor
    {
        private readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> _scopeStack = new();
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();
        private string? _currentProcScope;
        private readonly Dictionary<string, SqlType?> _variables = externalVariables is null
            ? new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SqlType?>(externalVariables, StringComparer.OrdinalIgnoreCase);

        public List<TypedPredicateFinding> Findings { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerivedFindings { get; } = [];

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
            base.ExplicitVisit(node);
            _scopeStack.Pop();
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
            // MergeSpecification subtree with this scope active.
            _scopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));
            base.ExplicitVisit(node);
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

            TryAddFinding(node.FirstExpression, node.SecondExpression, operatorText, node);
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

        public override void Visit(InPredicate node)
        {
            if (_scopeStack.Count == 0)
            {
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
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
                // A comparison outside any QuerySpecification's FROM scope: either a genuinely
                // scope-less comparison (a bare IF @x = 1, nothing to classify), or a WHERE
                // clause on an UPDATE/DELETE/MERGE statement, which this pass does not yet push
                // a scope for. Recorded rather than silently dropped, since the second case is a
                // real coverage gap, not a non-finding, and the two can't be told apart here.
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (a bare IF/WHILE condition, or another comparison genuinely outside any FROM clause)");
                return;
            }

            // Innermost scope first, then progressively outer ones - a correlated subquery's
            // predicate can legitimately reference an enclosing query's alias
            // (docs/audit-remediation-plan.md Phase 2.2).
            var scopeChain = _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            var left = ResolveOperand(first, scopeChain);
            var right = ResolveOperand(second, scopeChain);

            PredicateOperand.Column? column;
            PredicateOperand? other;
            if (left is PredicateOperand.Column leftColumn)
            {
                (column, other) = (leftColumn, right);
            }
            else if (right is PredicateOperand.Column rightColumn)
            {
                (column, other) = (rightColumn, left);
            }
            else
            {
                (column, other) = (null, null);
            }

            if (column is null || other is null)
            {
                return;
            }

            var otherType = other is PredicateOperand.Value value ? value.Type : ((PredicateOperand.Column)other).Type;
            var verdict = VerdictClassifier.Classify(column.Type, otherType);

            Findings.Add(new TypedPredicateFinding(verdict, column, other, operatorText, sourcePath, node.StartLine, node.StartColumn));
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

                default:
                    // Most commonly a function call (scalar UDF or builtin - neither has a return
                    // type registry; coverage-remediation-plan.md Phase 3.1/0.2), but also any
                    // other scalar expression kind this pass doesn't type. The operand still
                    // resolves Unknown, exactly as before - this only makes it counted instead of
                    // silently falling through.
                    ledger.Record(
                        AnalysisPass.Predicates, sourcePath, expression.StartLine, expression.StartColumn,
                        "predicate operand", $"operand of kind '{expression.GetType().Name}' has no type resolution - resolved Unknown");
                    return new PredicateOperand.Value(Type: null);
            }
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
                var type = ResolveOperand(value, scopeChain) switch
                {
                    PredicateOperand.Value v => v.Type,
                    PredicateOperand.Column c => c.Type,
                    _ => null,
                };

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
            return columns.Count == 0 ? null : ExtractScalarType(columns[0].Provenance);
        }

        // Never guesses: a Union only yields a usable type when every branch agrees, and an
        // Expression only when Pass 2 already inferred one - anything else (Unknown, a
        // disagreeing Union) surfaces as an unresolvable element to the caller.
        private static SqlType? ExtractScalarType(ColumnProvenance provenance) => provenance switch
        {
            ColumnProvenance.BaseColumn baseColumn => baseColumn.Type,
            ColumnProvenance.Declared declared => declared.Type,
            ColumnProvenance.Cast cast => cast.ExplicitType,
            ColumnProvenance.Expression expression => expression.InferredType,
            ColumnProvenance.Union union => AllBranchesAgree(union.Branches, out var agreedType) ? agreedType : null,
            _ => null,
        };

        private static bool AllBranchesAgree(IReadOnlyList<ColumnProvenance> branches, out SqlType? agreedType)
        {
            agreedType = null;
            foreach (var branch in branches)
            {
                var branchType = ExtractScalarType(branch);
                if (branchType is null)
                {
                    return false;
                }

                if (agreedType is null)
                {
                    agreedType = branchType;
                }
                else if (agreedType.Category != branchType.Category)
                {
                    return false;
                }
            }

            return agreedType is not null;
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
                return new PredicateOperand.Column(baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, indexed, baseColumn.Depth, baseColumn);
            }

            if (provenance is ColumnProvenance.Declared declared)
            {
                // A multi-statement TVF's own RETURNS TABLE(...) column (docs/audit-remediation-
                // plan.md Phase 4.2) - a real, known type, but never traceable to a real catalog
                // table or index (there is none - it's the function's internal table variable).
                return new PredicateOperand.Column(declared.TableQualifiedName ?? "?", columnName, declared.Type, Indexed: false, Depth: 0, declared);
            }

            if (ColumnProvenanceAnalysis.IsExpressionDerived(provenance))
            {
                RecordExpressionDerivedFinding(columnName, columnRef, provenance);
            }

            // Cast/Expression (reported above)/Union/Unknown/Declared - not eligible for the
            // type-precedence "indexed column" side of a verdict.
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
