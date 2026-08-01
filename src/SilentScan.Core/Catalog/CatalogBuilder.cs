using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Catalog;

/// <summary>
/// Pass 1: walks parsed .sql files and builds the <see cref="DatabaseCatalog"/> - tables,
/// columns, types, collations, indexes, PK/UQ, temp tables, table variables (CLAUDE.md Pass 1).
/// A <see cref="TSqlFragmentVisitor"/>-based visitor rather than a hand-rolled statement switch
/// (docs/audit-remediation-plan.md Phase 2.5): the default (un-overridden) ExplicitVisit
/// implementation already recurses into every container - procedure/function/trigger bodies,
/// IF/WHILE/TRY blocks, BEGIN/END blocks - so a construct nested inside any of them is never
/// silently missed just because nobody enumerated that specific container type.
/// </summary>
public static class CatalogBuilder
{
    /// <summary>
    /// Builds the catalog. <paramref name="manifestDeclaredCollation"/> is the corpus manifest's
    /// declaredCollation hint (CLAUDE.md Pass 1: "database default collation from any CREATE
    /// DATABASE/manifest hint") - used only as a last resort, when no scanned file contains an
    /// explicit CREATE DATABASE/ALTER DATABASE ... COLLATE statement of its own. Every string
    /// column's own collation always wins over either source.
    /// </summary>
    public static DatabaseCatalog Build(IEnumerable<SqlParseResult> parseResults, string? manifestDeclaredCollation = null)
    {
        var catalog = new DatabaseCatalog();
        var results = parseResults as IReadOnlyList<SqlParseResult> ?? parseResults.ToList();

        catalog.DefaultCollation = ResolveDefaultCollation(results, manifestDeclaredCollation);

        // CREATE TYPE ... FROM aliases must be known before ANY column resolves its type
        // (docs/audit-remediation-plan.md Phase 6.2) - the same cross-file-ordering problem
        // CollectTables solves for tables applies here too (a repo's type aliases routinely
        // live in their own file, sorted before or after the tables that use them).
        foreach (var result in results)
        {
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.CollectTypeAliases));
        }

        // Two-phase build (docs/audit-remediation-plan.md Phase 2.5): every CREATE TABLE across
        // every scanned file is cataloged before any ALTER TABLE/CREATE INDEX/SELECT INTO is
        // applied, so cross-file ordering (an index or ALTER declared in a file that sorts
        // before the file with the base CREATE TABLE - routine in real repos that split DDL
        // across per-object files) no longer drops real catalog data.
        foreach (var result in results)
        {
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.CollectTables));
        }

        foreach (var result in results)
        {
            Walk(result, new Visitor(catalog, result.SourcePath, BuildPhase.ApplyEverythingElse));
        }

        return catalog;
    }

    private static void Walk(SqlParseResult result, Visitor visitor)
    {
        if (result.Fragment is not TSqlScript script)
        {
            return;
        }

        foreach (var batch in script.Batches)
        {
            batch.Accept(visitor);
        }
    }

    /// <summary>
    /// Explicit DDL always wins over the manifest hint (CLAUDE.md Pass 1: "database default
    /// collation from any CREATE DATABASE/manifest hint" - DDL is the stronger signal of the
    /// two, since it's what the repo itself declares rather than an out-of-band annotation).
    /// </summary>
    private static Collation? ResolveDefaultCollation(IReadOnlyList<SqlParseResult> results, string? manifestDeclaredCollation)
    {
        if (FindExplicitDatabaseCollation(results) is { } explicitName)
        {
            return new Collation(explicitName, CollationSource.DatabaseDefaultFromDdl);
        }

        return manifestDeclaredCollation is { Length: > 0 }
            ? new Collation(manifestDeclaredCollation, CollationSource.DatabaseDefaultFromManifest)
            : null;
    }

    /// <summary>
    /// Scans every batch's top-level statements (not nested inside procedures/blocks - a
    /// database-level COLLATE statement is standalone DDL, never buried in a proc body) for the
    /// last CREATE DATABASE or ALTER DATABASE ... COLLATE, in file/statement order. Last-writer-
    /// wins, matching every other catalog merge in this pass - the tool assumes a single target
    /// database per scan, same simplification <see cref="SchemaObjectNameHelper"/> already makes.
    /// </summary>
    private static string? FindExplicitDatabaseCollation(IReadOnlyList<SqlParseResult> results)
    {
        string? found = null;
        foreach (var result in results)
        {
            if (result.Fragment is not TSqlScript script)
            {
                continue;
            }

            foreach (var statement in script.Batches.SelectMany(b => b.Statements))
            {
                var collation = statement switch
                {
                    CreateDatabaseStatement { Collation.Value: { } name } => name,
                    AlterDatabaseCollateStatement { Collation.Value: { } name } => name,
                    _ => null,
                };

                if (collation is not null)
                {
                    found = collation;
                }
            }
        }

        return found;
    }

    private enum BuildPhase
    {
        CollectTypeAliases,
        CollectTables,
        ApplyEverythingElse,
    }

    /// <summary>
    /// One traversal of one file's batches. <see cref="_currentScope"/> tracks the qualified
    /// name of the innermost enclosing procedure/function/trigger, if any, so a temp table or
    /// table variable declared inside one gets a catalog key scoped to it - two procedures'
    /// same-named-but-differently-shaped temp objects (a very common real-world pattern) must
    /// not clobber each other (docs/audit-remediation-plan.md Phase 2.5).
    /// </summary>
    private sealed class Visitor(DatabaseCatalog catalog, string sourcePath, BuildPhase phase) : TSqlFragmentVisitor
    {
        private string? _currentScope;

        public override void ExplicitVisit(CreateTypeUddtStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                VisitCreateTypeAlias(node);
            }

            node.AcceptChildren(this);
        }

        /// <summary>
        /// <c>CREATE TYPE ... AS TABLE</c> - a reusable column shape for table-valued parameters
        /// (coverage-remediation-plan.md Phase 3.2). Gated to the same phase as
        /// <c>CREATE TYPE ... FROM</c> aliases: a TVP parameter referencing this type can appear
        /// in any scanned file regardless of declaration order, so the shape must be known before
        /// <see cref="VisitScopedBody"/> processes any procedure/function parameter list.
        /// </summary>
        public override void ExplicitVisit(CreateTypeTableStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                var (schema, name) = SchemaObjectNameHelper.Resolve(node.Name);
                var (columns, indexesFromColumns) = BuildColumns(node.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath);
                var indexesFromConstraints = BuildIndexesFromTableConstraints(node.Definition.TableConstraints);

                catalog.AddOrReplace(new CatalogTable(
                    schema,
                    name,
                    CatalogTableKind.TableType,
                    columns,
                    [.. indexesFromColumns, .. indexesFromConstraints],
                    sourcePath,
                    node.StartLine));
            }

            node.AcceptChildren(this);
        }

        // CLR constructs (coverage-remediation-plan.md Phase 0.2/3.6): no assembly-backed type,
        // aggregate, or CLR UDT is modeled - the decision is to count and decline, never guess at
        // a shape that lives outside the scanned script. Gated to one phase so a file walked
        // multiple times (CollectTypeAliases/CollectTables/ApplyEverythingElse) doesn't triple-count.
        public override void ExplicitVisit(CreateAssemblyStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR assembly", $"'{node.Name.Value}': CREATE ASSEMBLY is not modeled - CLR types/functions/aggregates it backs resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateAggregateStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR aggregate", $"'{SchemaObjectNameHelper.Qualify(node.Name)}': CREATE AGGREGATE is not modeled - not usable in typed comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateTypeUdtStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR user-defined type", $"'{SchemaObjectNameHelper.Qualify(node.Name)}': CREATE TYPE ... EXTERNAL NAME is not modeled - columns of this type resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        // ALTER ASSEMBLY (re-pointing an existing CLR assembly's file/version) - same CLR
        // decline-to-model decision as CREATE ASSEMBLY. Found by the reflection backstop
        // (coverage-remediation-plan.md Phase 2.1) while auditing CREATE/ALTER parity - was
        // silently unhandled before this, unlike CREATE ASSEMBLY which was already ledgered.
        public override void ExplicitVisit(AlterAssemblyStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "CLR assembly", $"'{node.Name.Value}': ALTER ASSEMBLY is not modeled - CLR types/functions/aggregates it backs resolve Unknown");
            }

            node.AcceptChildren(this);
        }

        // ALTER INDEX (REBUILD/REORGANIZE/DISABLE/...) - also found by the reflection backstop.
        // Not modeled: this pass tracks which columns HAVE an index, not per-statement index
        // state changes. The precision-relevant case is ALTER INDEX ... DISABLE, which makes a
        // previously-seekable index genuinely unusable - underreporting that (still counting the
        // column Indexed=true) is the wrong direction for CLAUDE.md's precision discipline, but
        // implementing it needs index-name -> CatalogIndex state tracking this pass doesn't have
        // yet. Ledgered rather than silently dropped; tracked as follow-up work, not fixed here.
        public override void ExplicitVisit(AlterIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "ALTER INDEX", $"'{node.Name?.Value ?? "ALL"}' on '{SchemaObjectNameHelper.Qualify(node.OnName)}': index state changes (REBUILD/REORGANIZE/DISABLE) are not modeled - Indexed reflects only whether an index was ever created, not whether it is currently enabled");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (phase == BuildPhase.CollectTables)
            {
                VisitCreateTable(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterTableAdd(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableAlterColumnStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterColumn(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(AlterTableDropTableElementStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDropTableElements(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitCreateIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateColumnStoreIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitCreateColumnStoreIndex(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDeclareTableVariable(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse && node.Into is not null)
            {
                VisitSelectInto(node);
            }

            node.AcceptChildren(this);
        }

        // Every concrete CREATE/ALTER/CREATE OR ALTER procedure and function statement needs its
        // own override for the same reason Phase 2.3 needed one in TypedPredicateExtractor:
        // ScriptDOM's Accept() binds at compile time to the most specific ExplicitVisit overload
        // that exists, so overriding only the common ProcedureStatementBodyBase base type would
        // never fire for e.g. an AlterProcedureStatement node.
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitFunctionBody(node, node.Name, node.ReturnType);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        private void VisitScopedBody(TSqlFragment node, SchemaObjectName name)
        {
            var previous = _currentScope;
            _currentScope = SchemaObjectNameHelper.Qualify(name);

            if (phase == BuildPhase.ApplyEverythingElse && node is ProcedureStatementBodyBase { Parameters: { } parameters })
            {
                RegisterTableValuedParameters(parameters);
            }

            node.AcceptChildren(this);
            _currentScope = previous;
        }

        /// <summary>
        /// A table-valued parameter (<c>@Orders Website.OrderList READONLY</c>) declares its type
        /// as a <see cref="UserDataTypeReference"/> naming a <c>CREATE TYPE ... AS TABLE</c> shape
        /// (coverage-remediation-plan.md Phase 3.2) - registered under the parameter's own
        /// variable name, scoped to the enclosing procedure/function/trigger exactly like a body-
        /// declared table variable, so <c>FROM @Orders</c> anywhere in the body resolves through
        /// the identical <c>VariableTableReference</c> path Phase 3.4 wired up. A parameter whose
        /// type isn't a registered table type (a scalar type, or a table type this scan never saw)
        /// is silently not a TVP - nothing to register, not a gap.
        /// </summary>
        private void RegisterTableValuedParameters(IList<ProcedureParameter> parameters)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.DataType is not UserDataTypeReference userType)
                {
                    continue;
                }

                var typeQualifiedName = SchemaObjectNameHelper.Qualify(userType.Name);
                if (catalog.Find(typeQualifiedName) is not { Kind: CatalogTableKind.TableType } tableType)
                {
                    continue;
                }

                catalog.AddOrReplace(
                    tableType with { SchemaName = null, Name = parameter.VariableName.Value, Kind = CatalogTableKind.TableVariable },
                    _currentScope);
            }
        }

        /// <summary>
        /// A multi-statement TVF's <c>RETURNS @t TABLE(...)</c> is a <see cref="DeclareTableVariableBody"/>
        /// hanging off the return type, not a <see cref="DeclareTableVariableStatement"/> - so unlike
        /// an ordinary <c>DECLARE @t TABLE(...)</c> inside the body, it was never registered, and a
        /// predicate inside the body over <c>FROM @t</c> resolved to no known table (coverage-
        /// remediation-plan.md Phase 3.4). Registered under the function's own scope, the identical
        /// key <see cref="VisitDeclareTableVariable"/> uses for a body-declared temp object, before
        /// entering the body - a predicate against @t anywhere in the body needs it to already exist.
        /// A CLR TVF's <c>RETURNS TABLE(...)</c> has the same <see cref="TableValuedFunctionReturnType"/>
        /// shape but no <c>@variable</c> name at all (<c>VariableName</c> is null, <c>AsDefined</c> is
        /// false) - nothing to register under, and its EXTERNAL NAME body could never reference one
        /// anyway, so that case is skipped rather than guessed at.
        /// </summary>
        private void VisitFunctionBody(TSqlFragment node, SchemaObjectName name, FunctionReturnType returnType)
        {
            if (phase == BuildPhase.ApplyEverythingElse
                && returnType is TableValuedFunctionReturnType { DeclareTableVariableBody: { VariableName: { } variableName, Definition: { } definition } body })
            {
                var (columns, indexesFromColumns) = BuildColumns(definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath);
                var indexesFromConstraints = BuildIndexesFromTableConstraints(definition.TableConstraints);

                var returnTable = new CatalogTable(
                    SchemaName: null,
                    variableName.Value,
                    CatalogTableKind.TableVariable,
                    columns,
                    [.. indexesFromColumns, .. indexesFromConstraints],
                    sourcePath,
                    body.StartLine);

                catalog.AddOrReplace(returnTable, SchemaObjectNameHelper.Qualify(name));
            }

            VisitScopedBody(node, name);
        }

        private void VisitCreateTypeAlias(CreateTypeUddtStatement createType)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createType.Name);

            // CREATE TYPE ... FROM only ever references a built-in type (SQL Server doesn't
            // allow aliasing an alias), so the existing resolver handles it with no alias
            // lookup of its own - typeAliases: null here is not a missed case, just "this
            // resolution never needs one."
            var underlyingType = SqlTypeReferenceResolver.Resolve(createType.DataType, columnCollation: null, typeAliases: null);
            if (underlyingType is null)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createType.StartLine, createType.StartColumn,
                    "CREATE TYPE ... FROM", $"'{qualifiedName}': underlying type could not be resolved");
                return;
            }

            catalog.AddTypeAlias(qualifiedName, underlyingType);
        }

        private void VisitCreateTable(CreateTableStatement createTable)
        {
            if (createTable.Definition is null)
            {
                // CREATE TABLE ... AS CLONE OF or CTAS-only forms have no inline column list.
                return;
            }

            var (schema, name) = SchemaObjectNameHelper.Resolve(createTable.SchemaObjectName);
            var isTemp = schema is null;
            var kind = isTemp ? CatalogTableKind.TemporaryTable : CatalogTableKind.Table;

            var (columns, indexesFromColumns) = BuildColumns(createTable.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath);
            var indexesFromConstraints = BuildIndexesFromTableConstraints(createTable.Definition.TableConstraints);
            var allIndexes = (List<CatalogIndex>)[.. indexesFromColumns, .. indexesFromConstraints];
            columns = ApplyPrimaryKeyNotNull(columns, allIndexes);

            var table = new CatalogTable(
                schema,
                name,
                kind,
                columns,
                allIndexes,
                sourcePath,
                createTable.StartLine);

            // A temp table (#t) is scoped to its declaring procedure, same as a table variable -
            // it's just as invisible outside that procedure. A batch-level temp table (declared
            // in an ad-hoc script, not inside any proc) has no scope, matching pre-fix behavior.
            catalog.AddOrReplace(table, isTemp ? _currentScope : null);
        }

        private void VisitAlterTableAdd(AlterTableAddTableElementStatement alterTable)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(alterTable.SchemaObjectName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE ADD", qualifiedName, alterTable);
                return;
            }

            var (newColumns, indexesFromColumns) = BuildColumns(alterTable.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath);
            var newIndexes = BuildIndexesFromTableConstraints(alterTable.Definition.TableConstraints);
            var mergedColumns = (List<CatalogColumn>)[.. existing.Columns, .. newColumns];
            var mergedIndexes = (List<CatalogIndex>)[.. existing.Indexes, .. indexesFromColumns, .. newIndexes];

            // Scoped lookup AND scoped write-back (coverage-remediation-plan.md Phase 3.2, the
            // same bug class as the predicate-side index lookup fix above): an ALTER TABLE/CREATE
            // INDEX targeting a #temp table or table variable must find AND re-store it under the
            // same scoped key it was declared with, or the update either silently misses a scoped
            // entry (unscoped Find) or creates a stray unscoped duplicate while leaving the real,
            // scoped entry stale (unscoped AddOrReplace). Find is always safe with a scope - a
            // real table falls through to the unscoped lookup automatically - but AddOrReplace's
            // write-back scope must match how the row was ORIGINALLY stored (see WriteScopeFor),
            // or a real table altered from inside a proc body would get wrongly re-stored under a
            // scoped key of its own, orphaning its real unscoped entry.
            catalog.AddOrReplace(
                existing with
                {
                    Columns = ApplyPrimaryKeyNotNull(mergedColumns, mergedIndexes),
                    Indexes = mergedIndexes,
                },
                WriteScopeFor(existing));
        }

        /// <summary>The scope key an already-cataloged table must be re-stored under after an in-place update - only a temp table/table variable was ever stored scoped; a real table always lives at the unscoped key regardless of where the statement altering it appears.</summary>
        private string? WriteScopeFor(CatalogTable table) =>
            table.Kind is CatalogTableKind.TemporaryTable or CatalogTableKind.TableVariable ? _currentScope : null;

        private void VisitAlterColumn(AlterTableAlterColumnStatement alterColumn)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(alterColumn.SchemaObjectName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE ALTER COLUMN", qualifiedName, alterColumn);
                return;
            }

            var columnName = alterColumn.ColumnIdentifier.Value;
            var existingColumn = existing.FindColumn(columnName);
            if (existingColumn is null)
            {
                // ALTER COLUMN on a column this pass never saw declared (e.g. added by a
                // migration script this scan doesn't include) - nothing to replace.
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, alterColumn.StartLine, alterColumn.StartColumn,
                    "ALTER TABLE ALTER COLUMN", $"column '{columnName}' on '{qualifiedName}' not found in catalog");
                return;
            }

            // The exact bug this fixes (docs/audit-remediation-plan.md Phase 2.5): a migration
            // script that widens a column's type (e.g. varchar -> nvarchar) previously left the
            // ORIGINAL type in the catalog forever, producing wrong-direction findings on
            // precisely the pattern this tool exists to catch. An unresolvable target type nulls
            // the column so downstream goes UNKNOWN rather than keeping the stale value.
            var newType = SqlTypeReferenceResolver.Resolve(alterColumn.DataType, alterColumn.Collation, catalog.TypeAliases);
            if (newType is { IsStringFamily: true, Collation: null } && catalog.DefaultCollation is not null)
            {
                newType = newType with { Collation = catalog.DefaultCollation };
            }

            var updatedColumns = existing.Columns
                .Select(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase) ? c with { Type = newType } : c)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = updatedColumns }, WriteScopeFor(existing));
        }

        private void VisitDropTableElements(AlterTableDropTableElementStatement dropStatement)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(dropStatement.SchemaObjectName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER TABLE DROP", qualifiedName, dropStatement);
                return;
            }

            var droppedColumnNames = dropStatement.AlterTableDropTableElements
                .Where(e => e.TableElementType == TableElementType.Column)
                .Select(e => e.Name.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (droppedColumnNames.Count == 0)
            {
                // Only constraints/indexes were dropped, no columns - nothing to remove from
                // the column list. Dropping a constraint by name isn't tracked back to a
                // specific CatalogIndex today (constraints aren't named-and-keyed for lookup),
                // so the index list is left as-is rather than guessing which one it was.
                return;
            }

            var remainingColumns = existing.Columns.Where(c => !droppedColumnNames.Contains(c.Name)).ToList();
            catalog.AddOrReplace(existing with { Columns = remainingColumns }, WriteScopeFor(existing));
        }

        private void VisitCreateIndex(CreateIndexStatement createIndex)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createIndex.OnName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("CREATE INDEX", qualifiedName, createIndex);
                return;
            }

            var index = new CatalogIndex(
                createIndex.Name?.Value,
                CatalogIndexKind.Index,
                createIndex.Unique,
                [.. createIndex.Columns.Select(ColumnName)],
                [.. createIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
                IsFiltered: createIndex.FilterPredicate is not null);

            catalog.AddOrReplace(existing with { Indexes = [.. existing.Indexes, index] }, WriteScopeFor(existing));
        }

        private void VisitCreateColumnStoreIndex(CreateColumnStoreIndexStatement createIndex)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createIndex.OnName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("CREATE COLUMNSTORE INDEX", qualifiedName, createIndex);
                return;
            }

            var index = new CatalogIndex(
                createIndex.Name?.Value,
                CatalogIndexKind.Index,
                IsUnique: false,
                [.. createIndex.Columns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
                IncludedColumns: [],
                IsColumnstore: true);

            catalog.AddOrReplace(existing with { Indexes = [.. existing.Indexes, index] }, WriteScopeFor(existing));
        }

        private void VisitDeclareTableVariable(DeclareTableVariableStatement declareTableVar)
        {
            var body = declareTableVar.Body;
            if (body.Definition is null)
            {
                return;
            }

            // A table variable's columns technically default to tempdb's collation, not the
            // user database's - but this tool models a single target database per scan (the
            // same simplification SchemaObjectNameHelper makes), so the scanned database's
            // default is the closest available signal rather than leaving every unqualified
            // column UNKNOWN.
            var (columns, indexesFromColumns) = BuildColumns(body.Definition, catalog.DefaultCollation, catalog.TypeAliases, catalog.Skipped, sourcePath);
            var indexesFromConstraints = BuildIndexesFromTableConstraints(body.Definition.TableConstraints);

            var table = new CatalogTable(
                SchemaName: null,
                body.VariableName.Value,
                CatalogTableKind.TableVariable,
                columns,
                [.. indexesFromColumns, .. indexesFromConstraints],
                sourcePath,
                declareTableVar.StartLine);

            catalog.AddOrReplace(table, _currentScope);
        }

        private void VisitSelectInto(SelectStatement select)
        {
            var targetName = select.Into!;
            var (schema, name) = SchemaObjectNameHelper.Resolve(targetName);
            var isTemp = schema is null;

            var columns = SelectIntoColumnResolver.Resolve(select, catalog, _currentScope, sourcePath, catalog.Skipped);

            var table = new CatalogTable(
                schema,
                name,
                isTemp ? CatalogTableKind.TemporaryTable : CatalogTableKind.Table,
                columns,
                Indexes: [],
                sourcePath,
                select.StartLine);

            catalog.AddOrReplace(table, isTemp ? _currentScope : null);
        }

        private void RecordUnresolvedTarget(string constructKind, string qualifiedName, TSqlFragment node) =>
            catalog.Skipped.Record(
                AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                constructKind, $"target table '{qualifiedName}' not found in catalog (cross-file/cross-database reference, or failed base CREATE TABLE)");

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

        /// <summary>
        /// A table-level PRIMARY KEY constraint (CONSTRAINT PK_X PRIMARY KEY (Col1, Col2))
        /// implies NOT NULL on its key columns even with no explicit NOT NULL clause on the
        /// column itself - the inline column-level case is already handled in
        /// BuildColumnConstraints; this covers the out-of-line, table-level form
        /// (docs/audit-remediation-plan.md Phase 2.5, needed for the sys.columns diff to agree
        /// with the real server).
        /// </summary>
        private static List<CatalogColumn> ApplyPrimaryKeyNotNull(List<CatalogColumn> columns, List<CatalogIndex> indexes)
        {
            var primaryKeyColumns = indexes
                .Where(i => i.Kind == CatalogIndexKind.PrimaryKey)
                .SelectMany(i => i.KeyColumns)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (primaryKeyColumns.Count == 0)
            {
                return columns;
            }

            return [.. columns.Select(c => primaryKeyColumns.Contains(c.Name) ? c with { IsNullable = false } : c)];
        }
    }

    /// <summary>
    /// Exposed for <see cref="Lineage.ViewDefinitionExtractor"/>: a multi-statement TVF's
    /// RETURNS @t TABLE(...) is column-definition syntax identical to a table variable, and
    /// its declared columns become <see cref="Lineage.ColumnProvenance.Declared"/> provenance.
    /// </summary>
    public static IReadOnlyList<CatalogColumn> BuildColumnsForExternalUse(
        TableDefinition definition, Collation? defaultCollation, IReadOnlyDictionary<string, SqlType>? typeAliases = null, SkipLedger? ledger = null, string? sourcePath = null) =>
        BuildColumns(definition, defaultCollation, typeAliases, ledger, sourcePath).Columns;

    private static (List<CatalogColumn> Columns, List<CatalogIndex> InlineIndexes) BuildColumns(
        TableDefinition definition, Collation? defaultCollation, IReadOnlyDictionary<string, SqlType>? typeAliases, SkipLedger? ledger, string? sourcePath)
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

            var declaredType = columnDefinition.DataType;
            var resolvedType = declaredType is null ? null : SqlTypeReferenceResolver.Resolve(declaredType, columnDefinition.Collation, typeAliases);
            if (resolvedType is null && sourcePath is not null && declaredType is not null)
            {
                // A computed column with no declared type (its type comes from the expression,
                // out of scope here - a documented hard case, not a resolution failure) has a
                // null DataType and is intentionally excluded from this: nothing to have
                // resolved. What IS recorded: a table type (TVP shape), CLR UDT, or other
                // explicitly-declared type this resolver declines to guess at (coverage-
                // remediation-plan.md Phase 0.2) - the column still enters the catalog as
                // Unknown (VerdictClassifier already treats a null Type as Unknown, never a
                // guess), but until now nothing counted how often this happens, so it was safe
                // but invisible in the study's coverage accounting.
                ledger?.Record(
                    AnalysisPass.Catalog, sourcePath, columnDefinition.StartLine, columnDefinition.StartColumn,
                    "column type", $"column '{name}' has type '{SchemaObjectNameHelper.Qualify(declaredType.Name)}' which could not be resolved");
            }

            if (resolvedType is { IsStringFamily: true, Collation: null } && defaultCollation is not null)
            {
                // CLAUDE.md Pass 1: "database default collation from any CREATE DATABASE/
                // manifest hint" - applied only when the column itself carries no explicit
                // COLLATE, which always wins.
                resolvedType = resolvedType with { Collation = defaultCollation };
            }

            columns.Add(new CatalogColumn(
                name,
                resolvedType,
                isNullable,
                IsIdentity: columnDefinition.IdentityOptions is not null,
                IsComputed: columnDefinition.ComputedColumnExpression is not null,
                IsPersisted: columnDefinition.IsPersisted));
        }

        // Table-level INDEX (...) definitions - e.g. WWI's `INDEX [IX_...] ([Col])` inside a
        // CREATE TYPE ... AS TABLE - live in TableDefinition.Indexes, a collection entirely
        // separate from TableConstraints (PK/UNIQUE) and a column's own inline .Index. Found
        // while wiring up table-valued parameters (coverage-remediation-plan.md Phase 3.2) but
        // not specific to table types - this was never read for an ordinary CREATE TABLE either,
        // so a real index declared this way was silently invisible to Indexed lookups everywhere.
        inlineIndexes.AddRange(definition.Indexes.Select(i => BuildInlineIndex(i, columnName: string.Empty)));

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

                    // A PRIMARY KEY column is implicitly NOT NULL even with no explicit NOT
                    // NULL clause (needed for the sys.columns diff to agree with the real
                    // server - docs/audit-remediation-plan.md Phase 2.5).
                    if (unique.IsPrimaryKey)
                    {
                        isNullable = false;
                    }

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

    private static string ColumnName(ColumnWithSortOrder columnWithSortOrder) =>
        columnWithSortOrder.Column.MultiPartIdentifier.Identifiers[^1].Value;
}
