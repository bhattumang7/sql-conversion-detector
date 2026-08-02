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
    public static bool IsAllowed(TSqlStatement statement) => statement switch
    {
        IfStatement ifStatement =>
            (ifStatement.ThenStatement is null || IsAllowed(ifStatement.ThenStatement))
            && (ifStatement.ElseStatement is null || IsAllowed(ifStatement.ElseStatement)),

        BeginEndBlockStatement beginEnd =>
            beginEnd.StatementList.Statements.All(IsAllowed),

        _ => IsAllowedPredicateSet(statement) || AllowedStatementTypes.Contains(statement.GetType()),
    };

    /// <summary>Every statement type name in <paramref name="batch"/> that isn't on the whitelist, deduplicated (recursing through IF/BEGIN...END wrappers) - empty means the whole batch is deployable.</summary>
    public static IReadOnlyList<string> DisallowedStatementTypeNames(TSqlBatch batch)
    {
        var disallowed = new List<string>();
        foreach (var statement in batch.Statements)
        {
            CollectDisallowed(statement, disallowed);
        }

        return [.. disallowed.Distinct(StringComparer.Ordinal)];
    }

    private static void CollectDisallowed(TSqlStatement statement, List<string> disallowed)
    {
        switch (statement)
        {
            case IfStatement ifStatement:
                if (ifStatement.ThenStatement is not null)
                {
                    CollectDisallowed(ifStatement.ThenStatement, disallowed);
                }

                if (ifStatement.ElseStatement is not null)
                {
                    CollectDisallowed(ifStatement.ElseStatement, disallowed);
                }

                break;

            case BeginEndBlockStatement beginEnd:
                foreach (var inner in beginEnd.StatementList.Statements)
                {
                    CollectDisallowed(inner, disallowed);
                }

                break;

            default:
                if (!IsAllowedPredicateSet(statement) && !AllowedStatementTypes.Contains(statement.GetType()))
                {
                    disallowed.Add(statement.GetType().Name);
                }

                break;
        }
    }
}
