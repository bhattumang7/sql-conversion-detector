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
            var currentCtes = CurrentCteRelations();
            var ctes = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, resolvedViews, sourcePath, ledger, _currentProcScope);
            _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
            base.ExplicitVisit(node);
            _cteStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            _scopeStack.Push(FromScopeResolver.Resolve(node.FromClause, catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), _currentProcScope));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
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

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name);

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variables[declaration.VariableName.Value] = SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null);
            }

            base.ExplicitVisit(node);
        }

        private IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
            _cteStack.Count > 0 ? _cteStack.Peek() : EmptyCteRelations;

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

        private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name)
        {
            _variables.Clear();

            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentProcScope = previousScope;
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
                // BETWEEN decomposes into `>= lower AND <= upper`; the lower bound's
                // operator exercises the same column-side conversion behavior as the
                // predicate as a whole, so it stands in for oracle probing purposes.
                TryAddFinding(node.FirstExpression, node.SecondExpression, ">=", node);
            }
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
                _variables[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null);
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
                ledger.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, "comparison outside FROM scope", "no FROM scope in effect (bare IF, or UPDATE/DELETE/MERGE WHERE not yet supported)");
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
                    return new PredicateOperand.Value(Rules.LiteralTypeResolver.Resolve(literal));

                default:
                    return new PredicateOperand.Value(Type: null);
            }
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
                var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName) ?? false;
                return new PredicateOperand.Column(baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, indexed, baseColumn.Depth, baseColumn);
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
                .Select(bc => new UnderlyingBaseColumn(bc.TableQualifiedName, bc.ColumnName, catalog.Find(bc.TableQualifiedName)?.IsIndexedColumn(bc.ColumnName) ?? false))
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
