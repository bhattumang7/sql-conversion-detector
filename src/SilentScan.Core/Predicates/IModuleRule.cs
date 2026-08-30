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

    void OnEnterAssignmentSetClause(AssignmentSetClause node, ModuleWalker walker)
    {
    }

    void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
    {
    }

    void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker)
    {
    }

    void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
    {
    }

    void OnEnterBooleanNotExpression(BooleanNotExpression node, ModuleWalker walker)
    {
    }

    void OnLeaveBooleanNotExpression(BooleanNotExpression node, ModuleWalker walker)
    {
    }

    void OnEnterOperandPosition(TSqlFragment node, ModuleWalker walker)
    {
    }

    void OnLeaveOperandPosition(TSqlFragment node, ModuleWalker walker)
    {
    }

    void OnEnterWhereClause(WhereClause node, ModuleWalker walker)
    {
    }

    void OnLeaveWhereClause(WhereClause node, ModuleWalker walker)
    {
    }

    void OnEnterHavingClause(HavingClause node, ModuleWalker walker)
    {
    }

    void OnLeaveHavingClause(HavingClause node, ModuleWalker walker)
    {
    }

    void OnEnterJoinSearchCondition(QualifiedJoin node, ModuleWalker walker)
    {
    }

    void OnLeaveJoinSearchCondition(QualifiedJoin node, ModuleWalker walker)
    {
    }

    void OnEnterMergeSearchCondition(MergeSpecification node, ModuleWalker walker)
    {
    }

    void OnLeaveMergeSearchCondition(MergeSpecification node, ModuleWalker walker)
    {
    }

    void OnEnterMergeActionSearchCondition(MergeActionClause node, ModuleWalker walker)
    {
    }

    void OnLeaveMergeActionSearchCondition(MergeActionClause node, ModuleWalker walker)
    {
    }

    void OnBooleanComparisonExpression(BooleanComparisonExpression node, ModuleWalker walker)
    {
    }

    void OnBooleanTernaryExpression(BooleanTernaryExpression node, ModuleWalker walker)
    {
    }

    void OnLikePredicate(LikePredicate node, ModuleWalker walker)
    {
    }

    void OnInPredicate(InPredicate node, ModuleWalker walker)
    {
    }

    void OnSubqueryComparisonPredicate(SubqueryComparisonPredicate node, ModuleWalker walker)
    {
    }

    void OnEnterSelectSetVariable(SelectSetVariable node, ModuleWalker walker)
    {
    }

    void OnEnterWhileStatement(WhileStatement node, ModuleWalker walker)
    {
    }

    void OnEnterIfStatement(IfStatement node, ModuleWalker walker)
    {
    }

    void OnEnterBooleanBinaryExpression(BooleanBinaryExpression node, ModuleWalker walker)
    {
    }

    void OnEnterBinaryExpression(BinaryExpression node, ModuleWalker walker)
    {
    }

    void OnEnterUnaryExpression(UnaryExpression node, ModuleWalker walker)
    {
    }
}
