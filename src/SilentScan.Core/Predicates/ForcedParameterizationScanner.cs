using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ForcedParameterizationScanner
{
    public static IReadOnlyList<ForcedParameterizationFinding> Scan(IEnumerable<SqlParseResult> parseResults)
    {
        var findings = new List<ForcedParameterizationFinding>();
        foreach (var parseResult in parseResults)
        {
            var visitor = new Visitor(parseResult.SourcePath);
            parseResult.Fragment.Accept(visitor);
            findings.AddRange(visitor.Findings);
        }

        return
        [
            .. findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;
        private string currentModule;

        public Visitor(string sourcePath)
        {
            this.sourcePath = sourcePath;
            currentModule = sourcePath;
        }

        public List<ForcedParameterizationFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterFunctionStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            currentModule = SchemaObjectNameHelper.Qualify(node.Name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(LikePredicate node)
        {
            if (node.SecondExpression is Literal literal)
            {
                Add(ForcedParameterizationFindingKind.LikePatternLiteral, literal,
                    $"LIKE pattern '{LiteralText(literal)}' is a literal - the engine leaves it unparameterized even under PARAMETERIZATION FORCED, so a workload varying only this pattern still recompiles per distinct value.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TopRowFilter node)
        {
            if (node.Expression is Literal literal)
            {
                Add(ForcedParameterizationFindingKind.TopOrPagingLiteral, literal,
                    $"TOP row count '{LiteralText(literal)}' is a literal - the engine leaves it unparameterized even under PARAMETERIZATION FORCED, so a workload varying the row count still recompiles per distinct value.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OffsetClause node)
        {
            var literal = node.OffsetExpression as Literal ?? node.FetchExpression as Literal;
            if (literal is not null)
            {
                Add(ForcedParameterizationFindingKind.TopOrPagingLiteral, literal,
                    $"OFFSET/FETCH row count '{LiteralText(literal)}' is a literal - the engine leaves it unparameterized even under PARAMETERIZATION FORCED, so a workload varying the page size still recompiles per distinct value.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectScalarExpression node)
        {
            if (node.Expression is Literal literal)
            {
                Add(ForcedParameterizationFindingKind.SelectListLiteral, literal,
                    $"Select-list literal '{LiteralText(literal)}' stays unparameterized even under PARAMETERIZATION FORCED.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(HavingClause node)
        {
            foreach (var literal in FindDirectComparisonLiterals(node.SearchCondition))
            {
                Add(ForcedParameterizationFindingKind.HavingLiteral, literal,
                    $"HAVING comparand '{LiteralText(literal)}' is a literal - the engine leaves it unparameterized even under PARAMETERIZATION FORCED.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OrderByClause node)
        {
            foreach (var element in node.OrderByElements)
            {
                if (element.Expression is Literal)
                {
                    continue;
                }

                var finder = new LiteralFinder();
                element.Expression.Accept(finder);
                if (finder.Found is { } literal)
                {
                    Add(ForcedParameterizationFindingKind.OrderByExpressionLiteral, element,
                        $"ORDER BY expression contains literal '{LiteralText(literal)}' - the engine leaves it unparameterized even under PARAMETERIZATION FORCED.");
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GroupByClause node)
        {
            foreach (var specification in node.GroupingSpecifications.OfType<ExpressionGroupingSpecification>())
            {
                var finder = new LiteralFinder();
                specification.Expression.Accept(finder);
                if (finder.Found is { } literal)
                {
                    Add(ForcedParameterizationFindingKind.GroupByExpressionLiteral, specification,
                        $"GROUP BY expression contains literal '{LiteralText(literal)}' - the engine leaves it unparameterized even under PARAMETERIZATION FORCED.");
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is UserDefinedTypeCallTarget)
            {
                var literalArg = node.Parameters.OfType<Literal>().FirstOrDefault();
                if (literalArg is not null)
                {
                    Add(ForcedParameterizationFindingKind.DoubleColonCallArgumentLiteral, literalArg,
                        $"Literal argument '{LiteralText(literalArg)}' to a TypeName::Method(...) static call stays unparameterized even under PARAMETERIZATION FORCED.");
                }
            }
            else if (string.Equals(node.FunctionName?.Value, "CHECKSUM", StringComparison.OrdinalIgnoreCase))
            {
                var literalArg = node.Parameters.OfType<Literal>().FirstOrDefault();
                if (literalArg is not null)
                {
                    Add(ForcedParameterizationFindingKind.CheckSumArgumentLiteral, literalArg,
                        $"CHECKSUM(...) literal argument '{LiteralText(literalArg)}' stays unparameterized even under PARAMETERIZATION FORCED.");
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.TableSampleClause?.SampleNumber is Literal literal)
            {
                Add(ForcedParameterizationFindingKind.TableSampleSizeLiteral, literal,
                    $"TABLESAMPLE size '{LiteralText(literal)}' is a literal - the engine leaves it unparameterized even under PARAMETERIZATION FORCED.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OutputClause node)
        {
            foreach (var column in node.SelectColumns)
            {
                if (column is SelectScalarExpression { Expression: Literal literal })
                {
                    Add(ForcedParameterizationFindingKind.DmlOutputListLiteral, literal,
                        $"OUTPUT clause literal '{LiteralText(literal)}' stays unparameterized even under PARAMETERIZATION FORCED.");
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ConvertCall node)
        {
            if (node.Style is Literal literal)
            {
                Add(ForcedParameterizationFindingKind.ConvertStyleCodeLiteral, literal,
                    $"CONVERT style code '{LiteralText(literal)}' stays unparameterized even under PARAMETERIZATION FORCED.");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BinaryExpression node)
        {
            if (node.FirstExpression is Literal && node.SecondExpression is Literal)
            {
                Add(ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral, node,
                    "Constant-foldable literal expression parameterizes as separate parameters instead of one folded value under PARAMETERIZATION FORCED.",
                    FindingConfidence.Low);
            }

            base.ExplicitVisit(node);
        }

        private void Add(ForcedParameterizationFindingKind kind, TSqlFragment site, string detailText, FindingConfidence confidence = FindingConfidence.High) =>
            Findings.Add(new ForcedParameterizationFinding(
                kind, currentModule, sourcePath, site.StartLine, site.StartColumn, detailText, confidence));

        private static string LiteralText(Literal literal) => literal.Value ?? "NULL";

private sealed class LiteralFinder : TSqlFragmentVisitor
        {
            public Literal? Found { get; private set; }

            public override void ExplicitVisit(IntegerLiteral node) => Found ??= node;
            public override void ExplicitVisit(NumericLiteral node) => Found ??= node;
            public override void ExplicitVisit(RealLiteral node) => Found ??= node;
            public override void ExplicitVisit(MoneyLiteral node) => Found ??= node;
            public override void ExplicitVisit(BinaryLiteral node) => Found ??= node;
            public override void ExplicitVisit(StringLiteral node) => Found ??= node;
            public override void ExplicitVisit(NullLiteral node) => Found ??= node;
            public override void ExplicitVisit(IdentifierLiteral node) => Found ??= node;
            public override void ExplicitVisit(DefaultLiteral node) => Found ??= node;
            public override void ExplicitVisit(MaxLiteral node) => Found ??= node;
            public override void ExplicitVisit(OdbcLiteral node) => Found ??= node;
        }

private static IEnumerable<Literal> FindDirectComparisonLiterals(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanComparisonExpression comparison:
                    if (comparison.FirstExpression is Literal first)
                    {
                        yield return first;
                    }

                    if (comparison.SecondExpression is Literal second)
                    {
                        yield return second;
                    }

                    break;

                case BooleanBinaryExpression binary:
                    foreach (var literal in FindDirectComparisonLiterals(binary.FirstExpression))
                    {
                        yield return literal;
                    }

                    foreach (var literal in FindDirectComparisonLiterals(binary.SecondExpression))
                    {
                        yield return literal;
                    }

                    break;

                case BooleanParenthesisExpression parenthesis:
                    foreach (var literal in FindDirectComparisonLiterals(parenthesis.Expression))
                    {
                        yield return literal;
                    }

                    break;
            }
        }
    }
}
