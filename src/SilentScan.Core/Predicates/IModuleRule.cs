using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

internal interface IModuleRule
{
    void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
    {
    }

    void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
    {
    }

    void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker)
    {
    }

    void OnLeaveTriggerBody(TriggerStatementBody node, ModuleWalker walker)
    {
    }

    void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveSelectStatementScope(SelectStatement node, ModuleWalker walker)
    {
    }

    void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnLeaveQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnLeaveUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnLeaveDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnLeaveMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker)
    {
    }

    void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveInsertStatementScope(InsertStatement node, ModuleWalker walker)
    {
    }
}
