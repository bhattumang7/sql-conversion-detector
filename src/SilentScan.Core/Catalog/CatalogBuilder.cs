using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Pass 1: walks parsed .sql files and builds the <see cref="DatabaseCatalog"/> - tables,
/// columns, types, collations, indexes, PK/UQ (CLAUDE.md Pass 1).
/// </summary>
public static class CatalogBuilder
{
    public static DatabaseCatalog Build(IEnumerable<SqlParseResult> parseResults)
    {
        var catalog = new DatabaseCatalog();

        foreach (var result in parseResults)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                Visit(statement, catalog, result.SourcePath);
            }
        }

        return catalog;
    }

    private static void Visit(TSqlStatement statement, DatabaseCatalog catalog, string sourcePath)
    {
        switch (statement)
        {
            case CreateTableStatement createTable:
                VisitCreateTable(createTable, catalog, sourcePath);
                break;
            case AlterTableAddTableElementStatement alterTable:
                VisitAlterTableAdd(alterTable, catalog, sourcePath);
                break;
            case CreateIndexStatement createIndex:
                VisitCreateIndex(createIndex, catalog, sourcePath);
                break;
            case DeclareTableVariableStatement declareTableVar:
                VisitDeclareTableVariable(declareTableVar, catalog, sourcePath);
                break;
            case BeginEndBlockStatement beginEnd:
                foreach (var inner in beginEnd.StatementList.Statements)
                {
                    Visit(inner, catalog, sourcePath);
                }

                break;
        }
    }

    private static void VisitCreateTable(CreateTableStatement createTable, DatabaseCatalog catalog, string sourcePath)
    {
        if (createTable.Definition is null)
        {
            // CREATE TABLE ... AS CLONE OF or CTAS-only forms have no inline column list.
            return;
        }

        var (schema, name) = SchemaObjectNameHelper.Resolve(createTable.SchemaObjectName);
        var kind = schema is null ? CatalogTableKind.TemporaryTable : CatalogTableKind.Table;

        var (columns, indexesFromColumns) = BuildColumns(createTable.Definition);
        var indexesFromConstraints = BuildIndexesFromTableConstraints(createTable.Definition.TableConstraints);

        var table = new CatalogTable(
            schema,
            name,
            kind,
            columns,
            [.. indexesFromColumns, .. indexesFromConstraints],
            sourcePath,
            createTable.StartLine);

        catalog.AddOrReplace(table);
    }

    private static void VisitAlterTableAdd(AlterTableAddTableElementStatement alterTable, DatabaseCatalog catalog, string sourcePath)
    {
        var qualifiedName = SchemaObjectNameHelper.Qualify(alterTable.SchemaObjectName);
        var existing = catalog.Find(qualifiedName);
        if (existing is null)
        {
            // ALTER TABLE against a table we haven't seen DDL for (e.g. cross-file ordering,
            // or the base CREATE TABLE failed to parse) - nothing to merge into. Recorded
            // rather than dropped: this can silently mask indexed/typed columns downstream.
            catalog.Skipped.Record(
                AnalysisPass.Catalog, sourcePath, alterTable.StartLine, alterTable.StartColumn,
                "ALTER TABLE ADD", $"target table '{qualifiedName}' not found in catalog (cross-file ordering or failed base CREATE TABLE)");
            return;
        }

        var (newColumns, indexesFromColumns) = BuildColumns(alterTable.Definition);
        var newIndexes = BuildIndexesFromTableConstraints(alterTable.Definition.TableConstraints);

        var merged = existing with
        {
            Columns = [.. existing.Columns, .. newColumns],
            Indexes = [.. existing.Indexes, .. indexesFromColumns, .. newIndexes],
        };

        catalog.AddOrReplace(merged);
    }

    private static void VisitCreateIndex(CreateIndexStatement createIndex, DatabaseCatalog catalog, string sourcePath)
    {
        var qualifiedName = SchemaObjectNameHelper.Qualify(createIndex.OnName);
        var existing = catalog.Find(qualifiedName);
        if (existing is null)
        {
            catalog.Skipped.Record(
                AnalysisPass.Catalog, sourcePath, createIndex.StartLine, createIndex.StartColumn,
                "CREATE INDEX", $"target table '{qualifiedName}' not found in catalog (cross-file ordering or failed base CREATE TABLE)");
            return;
        }

        var index = new CatalogIndex(
            createIndex.Name?.Value,
            CatalogIndexKind.Index,
            createIndex.Unique,
            [.. createIndex.Columns.Select(ColumnName)],
            [.. createIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)]);

        catalog.AddOrReplace(existing with { Indexes = [.. existing.Indexes, index] });
    }

    private static void VisitDeclareTableVariable(DeclareTableVariableStatement declareTableVar, DatabaseCatalog catalog, string sourcePath)
    {
        var body = declareTableVar.Body;
        if (body.Definition is null)
        {
            return;
        }

        var (columns, indexesFromColumns) = BuildColumns(body.Definition);
        var indexesFromConstraints = BuildIndexesFromTableConstraints(body.Definition.TableConstraints);

        var table = new CatalogTable(
            SchemaName: null,
            body.VariableName.Value,
            CatalogTableKind.TableVariable,
            columns,
            [.. indexesFromColumns, .. indexesFromConstraints],
            sourcePath,
            declareTableVar.StartLine);

        catalog.AddOrReplace(table);
    }

    /// <summary>
    /// Exposed for <see cref="Lineage.ViewDefinitionExtractor"/>: a multi-statement TVF's
    /// RETURNS @t TABLE(...) is column-definition syntax identical to a table variable, and
    /// its declared columns become <see cref="Lineage.ColumnProvenance.Declared"/> provenance.
    /// </summary>
    public static IReadOnlyList<CatalogColumn> BuildColumnsForExternalUse(TableDefinition definition) =>
        BuildColumns(definition).Columns;

    private static (List<CatalogColumn> Columns, List<CatalogIndex> InlineIndexes) BuildColumns(TableDefinition definition)
    {
        var columns = new List<CatalogColumn>();
        var inlineIndexes = new List<CatalogIndex>();

        foreach (var columnDefinition in definition.ColumnDefinitions)
        {
            var name = columnDefinition.ColumnIdentifier.Value;
            var isNullable = BuildColumnConstraints(columnDefinition, name, inlineIndexes);

            if (columnDefinition.Index is { } inlineIndex)
            {
                inlineIndexes.Add(BuildInlineIndex(inlineIndex, name));
            }

            columns.Add(new CatalogColumn(
                name,
                SqlTypeReferenceResolver.Resolve(columnDefinition.DataType, columnDefinition.Collation),
                isNullable,
                IsIdentity: columnDefinition.IdentityOptions is not null,
                IsComputed: columnDefinition.ComputedColumnExpression is not null,
                IsPersisted: columnDefinition.IsPersisted));
        }

        return (columns, inlineIndexes);
    }

    /// <summary>Applies a column's inline constraints (NULL/NOT NULL, inline PK/UNIQUE), returning the resolved nullability.</summary>
    private static bool BuildColumnConstraints(ColumnDefinition columnDefinition, string columnName, List<CatalogIndex> inlineIndexes)
    {
        var isNullable = true;

        foreach (var constraint in columnDefinition.Constraints)
        {
            switch (constraint)
            {
                case NullableConstraintDefinition nullable:
                    isNullable = nullable.Nullable;
                    break;
                case UniqueConstraintDefinition unique:
                    inlineIndexes.Add(new CatalogIndex(
                        unique.ConstraintIdentifier?.Value,
                        unique.IsPrimaryKey ? CatalogIndexKind.PrimaryKey : CatalogIndexKind.UniqueConstraint,
                        IsUnique: true,
                        unique.Columns.Count > 0 ? [.. unique.Columns.Select(ColumnName)] : [columnName],
                        IncludedColumns: []));
                    break;
            }
        }

        return isNullable;
    }

    private static CatalogIndex BuildInlineIndex(IndexDefinition inlineIndex, string columnName) => new(
        inlineIndex.Name?.Value,
        CatalogIndexKind.Index,
        inlineIndex.Unique,
        inlineIndex.Columns.Count > 0 ? [.. inlineIndex.Columns.Select(ColumnName)] : [columnName],
        [.. inlineIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)]);

    private static List<CatalogIndex> BuildIndexesFromTableConstraints(IList<ConstraintDefinition> tableConstraints)
    {
        var indexes = new List<CatalogIndex>();

        foreach (var constraint in tableConstraints.OfType<UniqueConstraintDefinition>())
        {
            indexes.Add(new CatalogIndex(
                constraint.ConstraintIdentifier?.Value,
                constraint.IsPrimaryKey ? CatalogIndexKind.PrimaryKey : CatalogIndexKind.UniqueConstraint,
                IsUnique: true,
                [.. constraint.Columns.Select(ColumnName)],
                IncludedColumns: []));
        }

        return indexes;
    }

    private static string ColumnName(ColumnWithSortOrder columnWithSortOrder) =>
        columnWithSortOrder.Column.MultiPartIdentifier.Identifiers[^1].Value;
}
