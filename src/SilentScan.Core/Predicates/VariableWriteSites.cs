using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public static class VariableWriteSites
{
    public static IEnumerable<(string Name, TSqlFragment Site)> InStatement(TSqlStatement statement)
    {
        switch (statement)
        {
            case SetVariableStatement set:
                yield return (set.Variable.Name, set);
                break;

            case SelectStatement { QueryExpression: QuerySpecification spec }:
                foreach (var element in spec.SelectElements.OfType<SelectSetVariable>())
                {
                    yield return (element.Variable.Name, statement);
                }

                break;

            case DeclareVariableStatement declare:
                foreach (var element in declare.Declarations.Where(e => e.Value is not null))
                {
                    yield return (element.VariableName.Value, statement);
                }

                break;

            case FetchCursorStatement { IntoVariables: { } intoVariables }:
                foreach (var variable in intoVariables)
                {
                    yield return (variable.Name, statement);
                }

                break;

            case ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference procRef }:
                foreach (var parameter in procRef.Parameters)
                {
                    if (parameter is { IsOutput: true, ParameterValue: VariableReference variable })
                    {
                        yield return (variable.Name, statement);
                    }
                }

                break;
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
