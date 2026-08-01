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

        // Namespacing that real-world multi-schema corpora need to even parse/resolve
        // (CREATE SCHEMA audit) and object aliasing that resolves like a name, not logic.
        typeof(CreateSchemaStatement),
        typeof(CreateSynonymStatement),
        typeof(DropSynonymStatement),
    ];

    /// <summary>True if <paramref name="statement"/> is a kind this project's analysis passes actually consume.</summary>
    public static bool IsAllowed(TSqlStatement statement) => AllowedStatementTypes.Contains(statement.GetType());

    /// <summary>Every statement type name in <paramref name="batch"/> that isn't on the whitelist, deduplicated - empty means the whole batch is deployable.</summary>
    public static IReadOnlyList<string> DisallowedStatementTypeNames(TSqlBatch batch) =>
        [.. batch.Statements.Where(s => !IsAllowed(s)).Select(s => s.GetType().Name).Distinct(StringComparer.Ordinal)];
}
