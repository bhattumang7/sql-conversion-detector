using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Catalog;

internal static class ComputedColumnDeterminismChecker
{
    private static readonly HashSet<string> AlwaysNonDeterministicFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEWID", "NEWSEQUENTIALID", "GETDATE", "GETUTCDATE",
        "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET",
        "FORMAT", "PARSENAME",
    };

    public static bool IsNonDeterministic(ScalarExpression expression)
    {
        var visitor = new Visitor();
        expression.Accept(visitor);
        return visitor.Found;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(FunctionCall node)
        {
            var name = node.FunctionName.Value;
            if (AlwaysNonDeterministicFunctionNames.Contains(name)
                || (node.Parameters.Count == 0 && string.Equals(name, "RAND", StringComparison.OrdinalIgnoreCase)))
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AtTimeZoneCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ParameterlessCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }
    }
}
