using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal static class ConcatNullYieldsNullFlowResolver
{
    public static Dictionary<TSqlStatement, bool> Resolve(TSqlFragment fragment)
    {
        var map = new Dictionary<TSqlStatement, bool>(ReferenceEqualityComparer.Instance);
        if (fragment is not TSqlScript script)
        {
            return map;
        }

        var policy = new Policy(map);
        var state = default(SetOptionFlowState);
        foreach (var batch in script.Batches)
        {
            state = ProcedureBodyFlowWalker.Walk(batch.Statements, state with { Depth = 0 }, policy);
        }

        return map;
    }

    private sealed class Policy(Dictionary<TSqlStatement, bool> map) : SetOptionFlowPolicyBase
    {
        private static StatementList? GetScopedStatementList(TSqlStatement statement) => statement switch
        {
            ProcedureStatementBodyBase p => p.StatementList,
            TriggerStatementBody t => t.StatementList,
            _ => null,
        };

        public override SetOptionFlowState PerStatement(TSqlStatement statement, SetOptionFlowState state)
        {
            map[statement] = state.IsOff;

            if (GetScopedStatementList(statement) is { Statements: var nestedStatements })
            {
                ProcedureBodyFlowWalker.Walk(nestedStatements, default(SetOptionFlowState), this);
            }

            if (statement is PredicateSetStatement { Options: var options, IsOn: var isOn } && (options & SetOptions.ConcatNullYieldsNull) != 0)
            {
                return state with { IsOff = !isOn };
            }

            return state;
        }
    }
}
