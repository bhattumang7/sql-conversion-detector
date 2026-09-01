using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public static class VariableWriteSites
{
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    public static IEnumerable<(string Name, TSqlFragment Site, bool IsUnconditional)> InStatement(TSqlStatement statement)
    {
        switch (statement)
        {
            case SetVariableStatement set:
                yield return (set.Variable.Name, set, true);
                break;

            case SelectStatement { QueryExpression: QuerySpecification spec }:
                foreach (var element in spec.SelectElements.OfType<SelectSetVariable>())
                {
                    yield return (element.Variable.Name, statement, IsGuaranteedRow(spec, element));
                }

                break;

            case DeclareVariableStatement declare:
                foreach (var element in declare.Declarations.Where(e => e.Value is not null))
                {
                    yield return (element.VariableName.Value, statement, true);
                }

                break;

            case FetchCursorStatement { IntoVariables: { } intoVariables }:
                foreach (var variable in intoVariables)
                {
                    yield return (variable.Name, statement, true);
                }

                break;

            case ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference procRef }:
                foreach (var parameter in procRef.Parameters)
                {
                    if (parameter is { IsOutput: true, ParameterValue: VariableReference variable })
                    {
                        yield return (variable.Name, statement, true);
                    }
                }

                break;
        }
    }

    private static bool IsGuaranteedRow(QuerySpecification spec, SelectSetVariable element) =>
        spec.FromClause is null || (spec.GroupByClause is null && ContainsTopLevelAggregate(element.Expression));

    private static bool ContainsTopLevelAggregate(ScalarExpression expression)
    {
        var collector = new TopLevelAggregateCollector();
        expression.Accept(collector);
        return collector.Found;
    }

    private sealed class TopLevelAggregateCollector : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (AggregateFunctionNames.Contains(node.FunctionName.Value))
            {
                Found = true;
                return;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
            _ = node;
        }
    }

    public static ScalarExpression? DirectLiteralAssignment(TSqlStatement statement, string variableName)
    {
        switch (statement)
        {
            case DeclareVariableStatement declare:
                var element = declare.Declarations.FirstOrDefault(
                    e => string.Equals(e.VariableName.Value, variableName, StringComparison.OrdinalIgnoreCase));
                return element?.Value;

            case SetVariableStatement set when string.Equals(set.Variable.Name, variableName, StringComparison.OrdinalIgnoreCase):
                return set.AssignmentKind == AssignmentKind.Equals ? set.Expression : null;

            default:
                return null;
        }
    }
}
