using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
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
        var visitor = new Visitor(parseResult.SourcePath, catalog, resolvedViews, externalVariables);
        parseResult.Fragment.Accept(visitor);
        return new PredicateExtractionResult(visitor.Findings, visitor.ExpressionDerivedFindings);
    }

    private sealed class Visitor(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyDictionary<string, SqlType?>? externalVariables) : TSqlFragmentVisitor
    {
        private readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> _scopeStack = new();
        private readonly Dictionary<string, SqlType?> _variables = externalVariables is null
            ? new Dictionary<string, SqlType?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SqlType?>(externalVariables, StringComparer.OrdinalIgnoreCase);

        public List<TypedPredicateFinding> Findings { get; } = [];

        public List<ExpressionDerivedFinding> ExpressionDerivedFindings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            _scopeStack.Push(FromScopeResolver.Resolve(node.FromClause, catalog, resolvedViews, sourcePath));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            // Local declarations don't cross proc boundaries, so start fresh per proc.
            _variables.Clear();
            RecordParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            _variables.Clear();
            RecordParameters(node.Parameters);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                _variables[declaration.VariableName.Value] = SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null);
            }

            base.ExplicitVisit(node);
        }

        public override void Visit(BooleanComparisonExpression node) =>
            TryAddFinding(node.FirstExpression, node.SecondExpression, ToOperatorText(node.ComparisonType), node);

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

        private static string ToOperatorText(BooleanComparisonType comparisonType) => comparisonType switch
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
            _ => throw new NotImplementedException($"Unrecognized comparison operator: {comparisonType}"),
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
                // A comparison outside any FROM scope (e.g. a bare IF @x = 1) has no column
                // side to resolve; nothing to classify.
                return;
            }

            var (byAlias, ordered) = _scopeStack.Peek();
            var left = ResolveOperand(first, byAlias, ordered);
            var right = ResolveOperand(second, byAlias, ordered);

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

        private PredicateOperand ResolveOperand(ScalarExpression expression, Dictionary<string, ScopeEntry> byAlias, List<ScopeEntry> ordered)
        {
            switch (expression)
            {
                case ColumnReferenceExpression columnRef:
                    return ResolveColumnOperand(columnRef, byAlias, ordered);

                case VariableReference variableRef:
                    return new PredicateOperand.Value(_variables.GetValueOrDefault(variableRef.Name));

                case Literal literal:
                    return new PredicateOperand.Value(Rules.LiteralTypeResolver.Resolve(literal));

                default:
                    return new PredicateOperand.Value(Type: null);
            }
        }

        private PredicateOperand ResolveColumnOperand(ColumnReferenceExpression columnRef, Dictionary<string, ScopeEntry> byAlias, List<ScopeEntry> ordered)
        {
            var identifiers = columnRef.MultiPartIdentifier.Identifiers;
            var columnName = identifiers[^1].Value;

            ResolvedColumn? resolved;
            bool isViewLayer;
            if (identifiers.Count >= 2 && byAlias.TryGetValue(identifiers[^2].Value, out var entry))
            {
                resolved = entry.Relation.FindColumn(columnName);
                isViewLayer = entry.IsViewLayer;
            }
            else
            {
                var matches = ordered.Select(e => (Entry: e, Column: e.Relation.FindColumn(columnName))).Where(m => m.Column is not null).ToList();
                resolved = matches.Count == 1 ? matches[0].Column : null;
                isViewLayer = matches.Count == 1 && matches[0].Entry.IsViewLayer;
            }

            var provenance = resolved is null ? null : ScalarExpressionResolver.BumpDepthIfViewLayer(resolved.Provenance, isViewLayer);
            if (provenance is ColumnProvenance.BaseColumn baseColumn)
            {
                var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName) ?? false;
                return new PredicateOperand.Column(baseColumn.TableQualifiedName, baseColumn.ColumnName, baseColumn.Type, indexed, baseColumn.Depth, baseColumn);
            }

            if (provenance is not null && ColumnProvenanceAnalysis.IsExpressionDerived(provenance))
            {
                RecordExpressionDerivedFinding(columnName, columnRef, provenance);
            }

            // Cast/Expression (reported above)/Union/Unknown/Declared, or unresolved - not
            // eligible for the type-precedence "indexed column" side of a verdict.
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
