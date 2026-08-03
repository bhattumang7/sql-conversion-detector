using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Verify.Deployment;

/// <summary>
/// The "corpus DML is never executed, anywhere" guarantee (CLAUDE.md hard scope) previously
/// rested entirely on manifest curation - <c>ScriptDeployer</c> ran every batch in a
/// <c>ddlPaths</c> file verbatim as sa, so a schema file that also happened to contain a seed
/// INSERT, a stray EXEC, or a `USE master` would simply run. This is the code-level backstop:
/// only statement kinds the analysis passes themselves consume (tables, indexes, views/TVFs,
/// table types, schemas, synonyms) are allowed to deploy; every other statement kind in a batch
/// - DML, procedural logic, permissions, anything this project has no static-analysis use for -
/// is skipped and reported rather than executed.
/// </summary>
public static class DdlStatementWhitelist
{
    private static readonly HashSet<Type> AllowedStatementTypes =
    [
        // Pass 1 catalog (CatalogBuilder): tables, indexes, table types.
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

        // Pass 2 lineage (ViewDefinitionExtractor): views and inline/multi-statement TVFs.
        typeof(CreateViewStatement),
        typeof(AlterViewStatement),
        typeof(CreateOrAlterViewStatement),
        typeof(DropViewStatement),
        typeof(CreateFunctionStatement),
        typeof(AlterFunctionStatement),
        typeof(CreateOrAlterFunctionStatement),
        typeof(DropFunctionStatement),

        // Sequences back DEFAULT NEXT VALUE FOR column defaults (Wide World Importers' Tables/
        // *.sql ships these) - schema shape, not logic.
        typeof(CreateSequenceStatement),
        typeof(DropSequenceStatement),

        // Namespacing that real-world multi-schema corpora need to even parse/resolve
        // (CREATE SCHEMA audit) and object aliasing that resolves like a name, not logic.
        typeof(CreateSchemaStatement),
        typeof(CreateSynonymStatement),
        typeof(DropSynonymStatement),
    ];

    /// <summary>
    /// Procedure/trigger DEFINITIONS - never allowed for verify-corpus's own deployment (that
    /// caller works from parsed proc-body text directly and never needed a proc's own row in
    /// this deployment's <c>sys.sql_modules</c>), but required for the engine-authoritative
    /// corpus path (roadmap "make the corpus catalog engine-authoritative"), where CLAUDE.md
    /// explicitly requires module TEXT to come from the engine too, not just schema. A
    /// <c>CREATE PROCEDURE ... AS &lt;body&gt;</c> statement is itself pure DDL - it registers the
    /// definition's text in the catalog and never runs the body - so allowing it here does not
    /// weaken "corpus DML and procs are never executed" at all: an actual <c>EXEC</c>/DML
    /// statement inside that body is still caught and skipped by the SAME recursive whitelist
    /// check the body of a deployed view/function already gets, since <see cref="IsAllowed"/>
    /// only inspects the top-level batch statement kind - the body of a CREATE PROCEDURE is
    /// opaque T-SQL text to the deploying batch, not a separate statement this whitelist walks
    /// into (matching how a view's or function's own body was already opaque before this).
    /// </summary>
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

    /// <summary>
    /// A session-setting statement (<c>SET ANSI_NULLS ON</c>, <c>SET QUOTED_IDENTIFIER ON</c>) -
    /// the standard SSMS-scripted batch header ahead of a CREATE. No DML risk: it changes parser
    /// behavior for the rest of the batch, nothing about the database's data.
    /// </summary>
    private static bool IsAllowedPredicateSet(TSqlStatement statement) => statement is PredicateSetStatement;

    /// <summary>
    /// True if <paramref name="statement"/> is a kind this project's analysis passes actually
    /// consume - checked recursively through <c>IF ... [ELSE ...]</c> and <c>BEGIN...END</c>
    /// wrappers, since real-world installers overwhelmingly guard their DDL with
    /// <c>IF NOT EXISTS (...) CREATE TABLE ...</c> rather than issuing it bare (DNN Platform's
    /// *.SqlDataProvider files are essentially 100% of this shape). Checking only the top-level
    /// statement type used to reject every such batch outright, discarding the CREATE/INDEX
    /// underneath along with the IF - the code-level whitelist has no reason to be that blunt:
    /// an IF/BEGIN wrapper is control flow, not the kind of statement this whitelist exists to
    /// keep out (DML, procedural logic, permissions).
    /// </summary>
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

    /// <summary>Every statement type name in <paramref name="batch"/> that isn't on the whitelist, deduplicated (recursing through IF/BEGIN...END wrappers) - empty means the whole batch is deployable.</summary>
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
