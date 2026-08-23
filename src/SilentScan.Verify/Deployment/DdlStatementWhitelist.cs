using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Verify.Deployment;

public static class DdlStatementWhitelist
{
    private static readonly HashSet<Type> AllowedStatementTypes =
    [
        typeof(CreateTableStatement),
        typeof(AlterTableAddTableElementStatement),
        typeof(AlterTableAlterColumnStatement),
        typeof(AlterTableDropTableElementStatement),
        typeof(AlterTableConstraintModificationStatement),
        typeof(DropTableStatement),
        typeof(CreateIndexStatement),
        typeof(CreateColumnStoreIndexStatement),
        typeof(AlterIndexStatement),
        typeof(DropIndexStatement),
        typeof(CreateTypeTableStatement),
        typeof(CreateTypeUddtStatement),

        typeof(CreateViewStatement),
        typeof(AlterViewStatement),
        typeof(CreateOrAlterViewStatement),
        typeof(DropViewStatement),
        typeof(CreateFunctionStatement),
        typeof(AlterFunctionStatement),
        typeof(CreateOrAlterFunctionStatement),
        typeof(DropFunctionStatement),

        typeof(CreateSequenceStatement),
        typeof(DropSequenceStatement),

        typeof(CreateSchemaStatement),
        typeof(CreateSynonymStatement),
        typeof(DropSynonymStatement),
    ];

private static readonly HashSet<Type> ProcedureAndTriggerDefinitionTypes =
    [
        typeof(CreateProcedureStatement),
        typeof(AlterProcedureStatement),
        typeof(CreateOrAlterProcedureStatement),
        typeof(DropProcedureStatement),
        typeof(CreateTriggerStatement),
        typeof(AlterTriggerStatement),
        typeof(CreateOrAlterTriggerStatement),
        typeof(DropTriggerStatement),
    ];

private static bool IsAllowedPredicateSet(TSqlStatement statement) => statement is PredicateSetStatement;

public static bool IsAllowed(TSqlStatement statement, bool allowProcedureAndTriggerDefinitions = false) => statement switch
    {
        IfStatement ifStatement =>
            (ifStatement.ThenStatement is null || IsAllowed(ifStatement.ThenStatement, allowProcedureAndTriggerDefinitions))
            && (ifStatement.ElseStatement is null || IsAllowed(ifStatement.ElseStatement, allowProcedureAndTriggerDefinitions)),

        BeginEndBlockStatement beginEnd =>
            beginEnd.StatementList.Statements.All(s => IsAllowed(s, allowProcedureAndTriggerDefinitions)),

        _ => IsAllowedPredicateSet(statement)
            || AllowedStatementTypes.Contains(statement.GetType())
            || (allowProcedureAndTriggerDefinitions && ProcedureAndTriggerDefinitionTypes.Contains(statement.GetType())),
    };

public static IReadOnlyList<string> DisallowedStatementTypeNames(TSqlBatch batch, bool allowProcedureAndTriggerDefinitions = false)
    {
        var disallowed = new List<string>();
        foreach (var statement in batch.Statements)
        {
            CollectDisallowed(statement, allowProcedureAndTriggerDefinitions, disallowed);
        }

        return [.. disallowed.Distinct(StringComparer.Ordinal)];
    }

    private static void CollectDisallowed(TSqlStatement statement, bool allowProcedureAndTriggerDefinitions, List<string> disallowed)
    {
        switch (statement)
        {
            case IfStatement ifStatement:
                if (ifStatement.ThenStatement is not null)
                {
                    CollectDisallowed(ifStatement.ThenStatement, allowProcedureAndTriggerDefinitions, disallowed);
                }

                if (ifStatement.ElseStatement is not null)
                {
                    CollectDisallowed(ifStatement.ElseStatement, allowProcedureAndTriggerDefinitions, disallowed);
                }

                break;

            case BeginEndBlockStatement beginEnd:
                foreach (var inner in beginEnd.StatementList.Statements)
                {
                    CollectDisallowed(inner, allowProcedureAndTriggerDefinitions, disallowed);
                }

                break;

            default:
                if (!IsAllowedPredicateSet(statement)
                    && !AllowedStatementTypes.Contains(statement.GetType())
                    && !(allowProcedureAndTriggerDefinitions && ProcedureAndTriggerDefinitionTypes.Contains(statement.GetType())))
                {
                    disallowed.Add(statement.GetType().Name);
                }

                break;
        }
    }
}
