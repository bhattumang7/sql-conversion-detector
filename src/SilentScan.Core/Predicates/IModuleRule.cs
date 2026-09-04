using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public interface IModuleRule
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

    void OnEnterInsertMergeAction(InsertMergeAction node, ModuleWalker walker)
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

    void OnLeaveWhileStatement(WhileStatement node, ModuleWalker walker)
    {
    }

    void OnEnterIfStatement(IfStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveIfStatement(IfStatement node, ModuleWalker walker)
    {
    }

    void OnEnterTryCatchStatement(TryCatchStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveTryCatchStatement(TryCatchStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCatchBlock(TryCatchStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveCatchBlock(TryCatchStatement node, ModuleWalker walker)
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

    void OnEnterFromClause(FromClause node, ModuleWalker walker)
    {
    }

    void OnEnterAlterTableSwitchStatement(AlterTableSwitchStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterTableChangeTrackingModificationStatement(AlterTableChangeTrackingModificationStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateXmlSchemaCollectionStatement(CreateXmlSchemaCollectionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterXmlSchemaCollectionStatement(AlterXmlSchemaCollectionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterSchemaStatement(AlterSchemaStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterTableRebuildStatement(AlterTableRebuildStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterIndexStatement(AlterIndexStatement node, ModuleWalker walker)
    {
    }

    void OnEnterDeclareCursorStatement(DeclareCursorStatement node, ModuleWalker walker)
    {
    }

    void OnEnterStatementList(StatementList node, ModuleWalker walker)
    {
    }

    void OnEnterDeclareTableVariableStatement(DeclareTableVariableStatement node, ModuleWalker walker)
    {
    }

    void OnEnterBinaryQueryExpression(BinaryQueryExpression node, ModuleWalker walker)
    {
    }

    void OnEnterPredicateSetStatement(PredicateSetStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateViewStatement(CreateViewStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveCreateViewStatement(CreateViewStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterViewStatement(AlterViewStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveAlterViewStatement(AlterViewStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateTableStatement(CreateTableStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateExternalTableStatement(CreateExternalTableStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateIndexStatement(CreateIndexStatement node, ModuleWalker walker)
    {
    }

    void OnEnterTopRowFilter(TopRowFilter node, ModuleWalker walker)
    {
    }

    void OnEnterOffsetClause(OffsetClause node, ModuleWalker walker)
    {
    }

    void OnEnterSelectScalarExpression(SelectScalarExpression node, ModuleWalker walker)
    {
    }

    void OnEnterOrderByClause(OrderByClause node, ModuleWalker walker)
    {
    }

    void OnEnterGroupByClause(GroupByClause node, ModuleWalker walker)
    {
    }

    void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
    {
    }

    void OnEnterLeftFunctionCall(LeftFunctionCall node, ModuleWalker walker)
    {
    }

    void OnEnterRightFunctionCall(RightFunctionCall node, ModuleWalker walker)
    {
    }

    void OnEnterNamedTableReference(NamedTableReference node, ModuleWalker walker)
    {
    }

    void OnEnterUnpivotedTableReference(UnpivotedTableReference node, ModuleWalker walker)
    {
    }

    void OnEnterGlobalFunctionTableReference(GlobalFunctionTableReference node, ModuleWalker walker)
    {
    }

    void OnEnterSemanticTableReference(SemanticTableReference node, ModuleWalker walker)
    {
    }

    void OnEnterOutputClause(OutputClause node, ModuleWalker walker)
    {
    }

    void OnEnterConvertCall(ConvertCall node, ModuleWalker walker)
    {
    }

    void OnEnterCastCall(CastCall node, ModuleWalker walker)
    {
    }

    void OnEnterFetchCursorStatement(FetchCursorStatement node, ModuleWalker walker)
    {
    }

    void OnEnterOpenCursorStatement(OpenCursorStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCloseCursorStatement(CloseCursorStatement node, ModuleWalker walker)
    {
    }

    void OnEnterDeallocateCursorStatement(DeallocateCursorStatement node, ModuleWalker walker)
    {
    }

    void OnEnterPrintStatement(PrintStatement node, ModuleWalker walker)
    {
    }

    void OnEnterTableHint(TableHint node, ModuleWalker walker)
    {
    }

    void OnEnterSetTransactionIsolationLevelStatement(SetTransactionIsolationLevelStatement node, ModuleWalker walker)
    {
    }

    void OnEnterGlobalVariableExpression(GlobalVariableExpression node, ModuleWalker walker)
    {
    }

    void OnEnterGoToStatement(GoToStatement node, ModuleWalker walker)
    {
    }

    void OnEnterExecutableProcedureReference(ExecutableProcedureReference node, ModuleWalker walker)
    {
    }

    void OnEnterBeginEndBlockStatement(BeginEndBlockStatement node, ModuleWalker walker)
    {
    }

    void OnEnterParenthesisExpression(ParenthesisExpression node, ModuleWalker walker)
    {
    }

    void OnEnterBooleanParenthesisExpression(BooleanParenthesisExpression node, ModuleWalker walker)
    {
    }

    void OnEnterExecuteStatement(ExecuteStatement node, ModuleWalker walker)
    {
    }

    void OnEnterSetCommandStatement(SetCommandStatement node, ModuleWalker walker)
    {
    }

    void OnEnterOverClause(OverClause node, ModuleWalker walker)
    {
    }

    void OnEnterBeginTransactionStatement(BeginTransactionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCommitTransactionStatement(CommitTransactionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterRollbackTransactionStatement(RollbackTransactionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterSaveTransactionStatement(SaveTransactionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterWaitForStatement(WaitForStatement node, ModuleWalker walker)
    {
    }

    void OnEnterBackupDatabaseStatement(BackupDatabaseStatement node, ModuleWalker walker)
    {
    }

    void OnEnterRestoreStatement(RestoreStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateDatabaseStatement(CreateDatabaseStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveCreateProcedureStatement(CreateProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterProcedureStatement(AlterProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveAlterProcedureStatement(AlterProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateOrAlterProcedureStatement(CreateOrAlterProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveCreateOrAlterProcedureStatement(CreateOrAlterProcedureStatement node, ModuleWalker walker)
    {
    }

    void OnEnterSetRowCountStatement(SetRowCountStatement node, ModuleWalker walker)
    {
    }

    void OnEnterRevertStatement(RevertStatement node, ModuleWalker walker)
    {
    }

    void OnEnterBooleanComparisonExpressionScope(BooleanComparisonExpression node, ModuleWalker walker)
    {
    }

    void OnLeaveBooleanComparisonExpressionScope(BooleanComparisonExpression node, ModuleWalker walker)
    {
    }

    void OnEnterStringLiteral(StringLiteral node, ModuleWalker walker)
    {
    }

    void OnEnterTriggerStatementScope(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject, ModuleWalker walker)
    {
    }

    void OnEnterCreateOrAlterViewStatement(CreateOrAlterViewStatement node, ModuleWalker walker)
    {
    }

    void OnLeaveCreateOrAlterViewStatement(CreateOrAlterViewStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateFunctionStatement(CreateFunctionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterAlterFunctionStatement(AlterFunctionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterCreateOrAlterFunctionStatement(CreateOrAlterFunctionStatement node, ModuleWalker walker)
    {
    }

    void OnEnterReadTextStatement(ReadTextStatement node, ModuleWalker walker)
    {
    }

    void OnEnterWriteTextStatement(WriteTextStatement node, ModuleWalker walker)
    {
    }

    void OnEnterUpdateTextStatement(UpdateTextStatement node, ModuleWalker walker)
    {
    }
}
