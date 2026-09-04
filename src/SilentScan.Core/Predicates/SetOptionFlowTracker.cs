using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

internal static class SetOptionFlowTracker
{
    public static bool ComputeFinalOffState(TSqlFragment fragment, SetOptions flag)
    {
        if (fragment is not TSqlScript script)
        {
            return false;
        }

        var policy = new Policy(flag);
        var state = default(SetOptionFlowState);
        foreach (var batch in script.Batches)
        {
            state = ProcedureBodyFlowWalker.Walk(batch.Statements, state with { Depth = 0 }, policy);
        }

        return state.IsOff;
    }

    private sealed class Policy(SetOptions flag) : SetOptionFlowPolicyBase
    {
        public override SetOptionFlowState PerStatement(TSqlStatement statement, SetOptionFlowState state) =>
            statement is PredicateSetStatement { Options: var options, IsOn: var isOn } && (options & flag) != 0
                ? state with { IsOff = !isOn }
                : state;
    }
}
