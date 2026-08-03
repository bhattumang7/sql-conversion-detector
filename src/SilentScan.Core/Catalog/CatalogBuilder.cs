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
    private const string SpRenameConstructKind = "sp_rename";

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

        /// <summary>
        /// <c>CREATE SYNONYM name FOR target</c> - a pure name-&gt;name map with nothing else to
        /// resolve first, so it belongs in the same phase type aliases do: a synonym declared in
        /// a file that sorts after its consumers (routine in real repos that split DDL per
        /// object) must still be visible regardless of file order.
        /// </summary>
        public override void ExplicitVisit(CreateSynonymStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                VisitCreateSynonym(node);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(DropSynonymStatement node)
        {
            if (phase == BuildPhase.CollectTypeAliases)
            {
                foreach (var target in node.Objects)
                {
                    catalog.RemoveSynonym(SchemaObjectNameHelper.Qualify(target));
                }
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

        // Full-text/spatial/XML indexes and external (PolyBase) tables: ConstructCoverage.json
        // carried "Ledgered" rows for these four with verifiedBy: null and no code anywhere
        // actually recording them - a phantom claim (docs/coverage-remediation-plan.md's own
        // "Ledgered" status means "every occurrence reaches a SkipLedger entry", which was false
        // for all four). None of the four contribute a column shape or a seekable comparison
        // this tool classifies (spatial/XML/full-text indexes support their own predicate
        // families, not equality/range; an external table's columns live in a data source
        // outside the scanned repo), so there's nothing to model - but an occurrence should still
        // be counted rather than silently vanishing, exactly like the CLR constructs above.
        public override void ExplicitVisit(CreateFullTextIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "full-text index", $"'{SchemaObjectNameHelper.Qualify(node.OnName)}': CREATE FULLTEXT INDEX is not modeled - supports CONTAINS/FREETEXT, not the comparison operators this tool classifies");
            }

            node.AcceptChildren(this);
        }

        // Found by the reflection backstop (StatementVariantParityTests) the moment
        // CreateFullTextIndexStatement above got its own visitor - same out-of-scope reasoning,
        // reusing the identical "full-text index" construct kind.
        public override void ExplicitVisit(AlterFullTextIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "full-text index", $"'{SchemaObjectNameHelper.Qualify(node.OnName)}': ALTER FULLTEXT INDEX is not modeled - supports CONTAINS/FREETEXT, not the comparison operators this tool classifies");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateSpatialIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "spatial index", $"'{SchemaObjectNameHelper.Qualify(node.Object)}': CREATE SPATIAL INDEX is not modeled - supports spatial predicates (STIntersects etc.), not equality/range comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateXmlIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "XML index", $"CREATE XML INDEX on column '{node.XmlColumn.Value}' is not modeled - supports XQuery, not scalar comparisons");
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(CreateExternalTableStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "external table", $"'{SchemaObjectNameHelper.Qualify(node.SchemaObjectName)}': CREATE EXTERNAL TABLE is not modeled - column shapes live in the external data source, which this tool never connects to");
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
        // DISABLE/REBUILD flip CatalogIndex.IsDisabled (the only two that change whether the
        // engine can actually use the index); other alter types (REORGANIZE, SET, ...) never
        // affect seekability, so they're ledgered rather than modeled.
        public override void ExplicitVisit(AlterIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitAlterIndex(node);
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

        /// <summary>
        /// Gated to the SAME phase as <see cref="CreateTableStatement"/> (not <c>ApplyEverythingElse</c>,
        /// where every other DROP/ALTER in this file lives) so create/drop/recreate cycles across a
        /// corpus's migration-history files resolve correctly: both are collected in one pass, in
        /// file-then-statement order, before any ALTER/index/computed-column logic ever runs against
        /// the result. A dropped-and-never-recreated table simply isn't in the catalog by the time
        /// ApplyEverythingElse or any later pass looks for it - predicates against it resolve to the
        /// honest "no known DDL" ledger reason instead of a stale, possibly wrong-typed definition
        /// (the false-positive class this pass exists to close). A drop immediately followed by a
        /// recreate in the same file set still ends up with the recreated shape, because the second
        /// CreateTableStatement re-adds it after this removes it, in that same single ordered walk.
        /// </summary>
        public override void ExplicitVisit(DropTableStatement node)
        {
            if (phase == BuildPhase.CollectTables)
            {
                VisitDropTable(node);
            }

            node.AcceptChildren(this);
        }

        /// <summary>
        /// <c>DROP INDEX index ON table</c> (and the multi-clause <c>DROP INDEX a.ix1, b.ix2</c> form) -
        /// lives in <c>ApplyEverythingElse</c> alongside every other index mutation
        /// (<see cref="VisitCreateIndex"/>, <see cref="VisitAlterIndex"/>), so a script that creates an
        /// index, drops it, and later queries the column no longer counts it as indexed.
        /// </summary>
        public override void ExplicitVisit(DropIndexStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitDropIndex(node);
            }

            node.AcceptChildren(this);
        }

        /// <summary>
        /// <c>DROP FUNCTION</c> on a scalar UDF - the registry counterpart to
        /// <see cref="VisitFunctionBody"/>'s <c>AddScalarFunctionReturnType</c> call, in the same
        /// <c>ApplyEverythingElse</c> phase so create/drop ordering within one pass stays consistent.
        /// A dropped inline-TVF/MSTVF's entry lives in <see cref="Lineage.ViewDefinitionExtractor"/>'s
        /// own view/TVF registry, not here - this only ever removes a scalar return-type entry, and
        /// removing a name that was never a scalar UDF (e.g. it was a TVF, or unseen entirely) is a
        /// harmless no-op on a dictionary that never had the key.
        /// </summary>
        public override void ExplicitVisit(DropFunctionStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                foreach (var target in node.Objects)
                {
                    catalog.RemoveScalarFunctionReturnType(SchemaObjectNameHelper.Qualify(target));
                }
            }

            node.AcceptChildren(this);
        }

        /// <summary>
        /// <c>EXEC sp_rename 'objname', 'newname'[, 'objtype']</c> - the one system procedure whose
        /// effect changes catalog identity rather than data, so it is modeled here rather than left to
        /// the dynamic-SQL pipeline (which only ever analyzes predicates, never DDL side effects).
        /// Only literal string arguments are handled, matching every DDL string in this file: a
        /// variable/expression argument makes the actual rename target undecidable without executing
        /// the script, so it is ledgered rather than guessed, and the catalog keeps the PRE-rename
        /// definition (never a false-positive risk - a later reference to the new name simply resolves
        /// "no known DDL" instead of silently inheriting the wrong shape). Gated to
        /// <c>ApplyEverythingElse</c>, alongside every other in-place catalog mutation.
        /// </summary>
        public override void ExplicitVisit(ExecuteStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                VisitPossibleSpRename(node);
            }

            node.AcceptChildren(this);
        }

        /// <summary>
        /// <c>USE &lt;database&gt;</c> - genuine cross-database switching mid-scan is not modeled
        /// (checked against the real pinned 5-repo corpus: no repo actually cross-references
        /// between two databases it also declares DDL for - mojoportal's two distinct USE targets
        /// are each a standalone script naming its own single dev database, never referencing the
        /// other - so building unverified resolution logic for a pattern nothing in the corpus
        /// exercises would be exactly the kind of speculative complexity this project's precision-
        /// first, oracle-verified-only discipline exists to avoid). Ledgered rather than silently
        /// swallowed - unlike before this pass, USE is no longer a construct with zero trace in
        /// either the ledger or the coverage matrix.
        /// </summary>
        public override void ExplicitVisit(UseStatement node)
        {
            if (phase == BuildPhase.ApplyEverythingElse)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    "USE", $"'{node.DatabaseName.Value}': database context switching is not modeled - every scanned object still resolves against the single implicit target database");
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

                // Triggers reach this same method via VisitScopedBody but are never a
                // ProcedureStatementBodyBase (they take no parameters), so this only ever
                // registers a real CREATE/ALTER PROCEDURE's own signature - the foundation the
                // procedure call graph (Predicates.ProcCallGraphBuilder) matches EXEC call sites'
                // arguments against.
                if (node is CreateProcedureStatement or AlterProcedureStatement or CreateOrAlterProcedureStatement)
                {
                    RegisterProcedureParameters(parameters);
                }
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
        /// Every declared parameter, scalar or table-valued, keyed under the procedure's own
        /// qualified name (<c>_currentScope</c>, already set by the caller) - declaration order
        /// preserved, since a positional <c>EXEC proc @a, @b</c> call site can only match
        /// arguments to formals by that order, and a TVP counts as a positional slot exactly
        /// like a scalar one even though it has no <see cref="SqlType"/> of its own (recorded
        /// with a null type, same as any other parameter this pass couldn't type - never simply
        /// omitted, which would shift every later formal's position out of alignment with the
        /// real declaration).
        /// </summary>
        private void RegisterProcedureParameters(IList<ProcedureParameter> parameters)
        {
            var registered = new List<ProcedureParameterInfo>(parameters.Count);
            foreach (var parameter in parameters)
            {
                var isTableValued = parameter.DataType is UserDataTypeReference userType
                    && catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is { Kind: CatalogTableKind.TableType };
                var resolvedType = isTableValued ? null : SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
                registered.Add(new ProcedureParameterInfo(parameter.VariableName.Value, resolvedType, parameter.Modifier == ParameterModifier.Output));
            }

            catalog.AddProcedureParameters(_currentScope!, registered);
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
            if (phase == BuildPhase.ApplyEverythingElse && returnType is ScalarFunctionReturnType scalarReturn)
            {
                var resolvedType = SqlTypeReferenceResolver.Resolve(scalarReturn.DataType, columnCollation: null, catalog.TypeAliases);
                if (resolvedType is { IsStringFamily: true, Collation: null } && catalog.DefaultCollation is not null)
                {
                    resolvedType = resolvedType with { Collation = catalog.DefaultCollation };
                }

                catalog.AddScalarFunctionReturnType(SchemaObjectNameHelper.Qualify(name), resolvedType);
            }

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

        private void VisitCreateSynonym(CreateSynonymStatement createSynonym)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(createSynonym.Name);

            // SchemaObjectNameHelper.Qualify only reads Database/Schema/Base - it silently
            // drops a ServerIdentifier, so a four-part linked-server target
            // (FOR linkedserver.otherdb.dbo.T) would otherwise collide with an unrelated local
            // key sharing the same database.schema.name tail. Ledgered, never registered under
            // a name that could alias the wrong object.
            if (createSynonym.ForName.ServerIdentifier is { Value.Length: > 0 })
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createSynonym.StartLine, createSynonym.StartColumn,
                    "CREATE SYNONYM", $"'{qualifiedName}': FOR target names a linked server - four-part cross-server synonyms are not modeled");
                return;
            }

            catalog.AddSynonym(qualifiedName, SchemaObjectNameHelper.Qualify(createSynonym.ForName));
        }

        private void VisitAlterIndex(AlterIndexStatement alterIndex)
        {
            if (alterIndex.AlterIndexType is not (AlterIndexType.Disable or AlterIndexType.Rebuild))
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, alterIndex.StartLine, alterIndex.StartColumn,
                    "ALTER INDEX", $"'{alterIndex.Name?.Value ?? "ALL"}' on '{SchemaObjectNameHelper.Qualify(alterIndex.OnName)}': {alterIndex.AlterIndexType} does not affect seekability and is not modeled");
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(alterIndex.OnName);
            var existing = catalog.Find(qualifiedName, _currentScope);
            if (existing is null)
            {
                RecordUnresolvedTarget("ALTER INDEX", qualifiedName, alterIndex);
                return;
            }

            var targetDisabledState = alterIndex.AlterIndexType == AlterIndexType.Disable;
            var indexName = alterIndex.Name?.Value;

            var updatedIndexes = existing.Indexes
                .Select(i => indexName is null || string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase)
                    ? i with { IsDisabled = targetDisabledState }
                    : i)
                .ToList();

            catalog.AddOrReplace(existing with { Indexes = updatedIndexes }, WriteScopeFor(existing));
        }

        private void VisitDropTable(DropTableStatement dropTable)
        {
            foreach (var target in dropTable.Objects)
            {
                var qualifiedName = SchemaObjectNameHelper.Qualify(target);
                if (catalog.Find(qualifiedName, _currentScope) is not { } existing)
                {
                    // IF EXISTS or a target outside this scan's file set - nothing to remove, but
                    // still worth an honest ledger entry: a caller diffing the ledger for "why did
                    // this table disappear" should be able to find this either way.
                    RecordUnresolvedTarget("DROP TABLE", qualifiedName, dropTable);
                    continue;
                }

                catalog.Remove(qualifiedName, WriteScopeFor(existing));
            }
        }

        private void VisitDropIndex(DropIndexStatement dropIndex)
        {
            foreach (var clause in dropIndex.DropIndexClauses)
            {
                // Two ScriptDOM shapes: the modern `DROP INDEX ix ON table` (Object + Index
                // separate) and the SQL 2000-era `DROP INDEX table.ix` form, whose ChildObjectName
                // carries the table as its base name and the index as ChildIdentifier - both
                // resolve to the same (table, indexName) pair once unwrapped.
                var (tableName, indexName) = clause switch
                {
                    DropIndexClause modern => (modern.Object, modern.Index.Value),
                    BackwardsCompatibleDropIndexClause legacy => (legacy.Index, legacy.Index.ChildIdentifier.Value),
                    _ => (null, null),
                };

                if (tableName is null || indexName is null)
                {
                    catalog.Skipped.Record(
                        AnalysisPass.Catalog, sourcePath, dropIndex.StartLine, dropIndex.StartColumn,
                        "DROP INDEX", $"clause of kind '{clause.GetType().Name}' is not modeled");
                    continue;
                }

                var qualifiedName = SchemaObjectNameHelper.Qualify(tableName);
                var existing = catalog.Find(qualifiedName, _currentScope);
                if (existing is null)
                {
                    RecordUnresolvedTarget("DROP INDEX", qualifiedName, dropIndex);
                    continue;
                }

                var remainingIndexes = existing.Indexes
                    .Where(i => !string.Equals(i.Name, indexName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (remainingIndexes.Count == existing.Indexes.Count)
                {
                    catalog.Skipped.Record(
                        AnalysisPass.Catalog, sourcePath, dropIndex.StartLine, dropIndex.StartColumn,
                        "DROP INDEX", $"index '{indexName}' on '{qualifiedName}' not found in catalog");
                    continue;
                }

                catalog.AddOrReplace(existing with { Indexes = remainingIndexes }, WriteScopeFor(existing));
            }
        }

        /// <summary>Object types <c>sp_rename</c> accepts in its third argument (case-insensitive); anything else (e.g. <c>USERDATATYPE</c>) is ledgered rather than modeled.</summary>
        private void VisitPossibleSpRename(ExecuteStatement execute)
        {
            if (execute.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference
                {
                    ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: { } procName,
                } procedureReference
                || !string.Equals(procName, SpRenameConstructKind, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryResolveSpRenameArguments(procedureReference.Parameters, out var objName, out var newName, out var objType))
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, execute.StartLine, execute.StartColumn,
                    SpRenameConstructKind, "objname/newname argument is not a literal string - rename not applied, catalog keeps the pre-rename definition");
                return;
            }

            if (objType is null || string.Equals(objType, "OBJECT", StringComparison.OrdinalIgnoreCase))
            {
                RenameTable(objName, newName, execute);
            }
            else if (string.Equals(objType, "COLUMN", StringComparison.OrdinalIgnoreCase))
            {
                RenameColumn(objName, newName, execute);
            }
            else if (string.Equals(objType, "INDEX", StringComparison.OrdinalIgnoreCase))
            {
                RenameIndex(objName, newName, execute);
            }
            else
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, execute.StartLine, execute.StartColumn,
                    SpRenameConstructKind, $"object type '{objType}' is not modeled");
            }
        }

        private static bool TryResolveSpRenameArguments(IList<ExecuteParameter> parameters, out string objName, out string newName, out string? objType)
        {
            objName = string.Empty;
            newName = string.Empty;
            objType = null;

            // sp_rename's arguments can be passed positionally or by @-name; either way, only a
            // literal string argument is honored - a variable/expression makes the real target
            // undecidable without executing the script.
            string? Resolve(int position, string parameterName) =>
                parameters
                    .Select((p, i) => (Param: p, Index: i))
                    .Where(x => string.Equals(x.Param.Variable?.Name, parameterName, StringComparison.OrdinalIgnoreCase)
                        || (x.Param.Variable is null && x.Index == position))
                    .Select(x => (x.Param.ParameterValue as StringLiteral)?.Value)
                    .FirstOrDefault(v => v is not null);

            if (Resolve(0, "@objname") is not { } resolvedObjName || Resolve(1, "@newname") is not { } resolvedNewName)
            {
                return false;
            }

            objName = resolvedObjName;
            newName = resolvedNewName;
            objType = Resolve(2, "@objtype");
            return true;
        }

        private void RenameTable(string objName, string newName, TSqlFragment node)
        {
            var (schema, oldQualifiedName) = SplitTableTarget(objName);
            if (schema is UnresolvableSchema)
            {
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, node.StartLine, node.StartColumn,
                    SpRenameConstructKind, $"'{objName}': three-part (database-qualified) rename target is not modeled");
                return;
            }

            if (catalog.Find(oldQualifiedName, _currentScope) is not { } existing)
            {
                RecordUnresolvedTarget(SpRenameConstructKind, oldQualifiedName, node);
                return;
            }

            var writeScope = WriteScopeFor(existing);
            catalog.Remove(oldQualifiedName, writeScope);
            catalog.AddOrReplace(existing with { SchemaName = schema, Name = newName }, writeScope);
        }

        private void RenameColumn(string objName, string newName, TSqlFragment node)
        {
            var (containerName, oldColumnName) = SplitLastSegment(objName);
            var (schema, tableQualifiedName) = SplitTableTarget(containerName);
            if (schema is UnresolvableSchema || catalog.Find(tableQualifiedName, _currentScope) is not { } existing)
            {
                RecordUnresolvedTarget("sp_rename (COLUMN)", tableQualifiedName, node);
                return;
            }

            var updatedColumns = existing.Columns
                .Select(c => string.Equals(c.Name, oldColumnName, StringComparison.OrdinalIgnoreCase) ? c with { Name = newName } : c)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = updatedColumns }, WriteScopeFor(existing));
        }

        private void RenameIndex(string objName, string newName, TSqlFragment node)
        {
            var (containerName, oldIndexName) = SplitLastSegment(objName);
            var (schema, tableQualifiedName) = SplitTableTarget(containerName);
            if (schema is UnresolvableSchema || catalog.Find(tableQualifiedName, _currentScope) is not { } existing)
            {
                RecordUnresolvedTarget("sp_rename (INDEX)", tableQualifiedName, node);
                return;
            }

            var updatedIndexes = existing.Indexes
                .Select(i => string.Equals(i.Name, oldIndexName, StringComparison.OrdinalIgnoreCase) ? i with { Name = newName } : i)
                .ToList();

            catalog.AddOrReplace(existing with { Indexes = updatedIndexes }, WriteScopeFor(existing));
        }

        /// <summary>Sentinel schema value <see cref="SplitTableTarget"/> returns for a three-part (database-qualified) target - cross-database rename resolution is out of scope, matching every other cross-database simplification in this pass.</summary>
        private const string UnresolvableSchema = "\0unresolvable";

        /// <summary>Splits an sp_rename object-name argument's leading table reference into (schema, qualifiedName), defaulting an unqualified name to dbo and a leading-<c>#</c> name to no schema at all - the same two defaults <see cref="SchemaObjectNameHelper.Resolve"/> applies to a real parsed SchemaObjectName, reimplemented here because sp_rename's arguments are plain string literals, not AST nodes.</summary>
        private static (string? Schema, string QualifiedName) SplitTableTarget(string name)
        {
            if (name.StartsWith('#'))
            {
                return (null, name);
            }

            var parts = name.Split('.');
            return parts.Length switch
            {
                1 => (SchemaObjectNameHelper.DefaultSchema, $"{SchemaObjectNameHelper.DefaultSchema}.{parts[0]}"),
                2 => (parts[0], name),
                _ => (UnresolvableSchema, name),
            };
        }

        /// <summary>Splits "a.b.c" into ("a.b", "c") - the container-plus-element shape sp_rename's COLUMN/INDEX forms use.</summary>
        private static (string Container, string Element) SplitLastSegment(string name)
        {
            var lastDot = name.LastIndexOf('.');
            return lastDot < 0 ? (name, name) : (name[..lastDot], name[(lastDot + 1)..]);
        }

        private void VisitCreateTable(CreateTableStatement createTable)
        {
            if (createTable.Definition is null)
            {
                // CREATE TABLE ... AS CLONE OF or CTAS-only forms have no inline column list -
                // ledgered rather than silently skipped, since the object's real shape is
                // determinable in principle (from the source table/query) but this pass doesn't
                // attempt it.
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, createTable.StartLine, createTable.StartColumn,
                    "CREATE TABLE", $"'{SchemaObjectNameHelper.Qualify(createTable.SchemaObjectName)}': no inline column list (CTAS / AS CLONE OF form) - column shape not modeled");
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

            // A PK/UNIQUE constraint's backing index is deterministically identifiable by name
            // (CatalogIndex.Name is set from the constraint's own ConstraintIdentifier in
            // BuildIndexesFromTableConstraints) - unlike an anonymous/system-named constraint,
            // whose CatalogIndex.Name never matched anything to begin with, so this removal is
            // a no-op for it rather than a guess. TableElementType.Index covers a plain
            // `DROP INDEX ix_name` element inside the same ALTER TABLE DROP list.
            var droppedConstraintOrIndexNames = dropStatement.AlterTableDropTableElements
                .Where(e => e.TableElementType is TableElementType.Constraint or TableElementType.Index)
                .Select(e => e.Name.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (droppedColumnNames.Count == 0 && droppedConstraintOrIndexNames.Count == 0)
            {
                return;
            }

            var remainingColumns = droppedColumnNames.Count == 0
                ? existing.Columns
                : existing.Columns.Where(c => !droppedColumnNames.Contains(c.Name)).ToList();

            var remainingIndexes = droppedConstraintOrIndexNames.Count == 0
                ? existing.Indexes
                : existing.Indexes.Where(i => i.Name is null || !droppedConstraintOrIndexNames.Contains(i.Name)).ToList();

            catalog.AddOrReplace(existing with { Columns = remainingColumns, Indexes = remainingIndexes }, WriteScopeFor(existing));
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
                // Should not happen for a syntactically valid DECLARE @t TABLE(...) - ScriptDom's
                // grammar requires a parenthesized definition - but ledgered defensively rather
                // than silently returning, matching this pass's own policy everywhere else.
                catalog.Skipped.Record(
                    AnalysisPass.Catalog, sourcePath, declareTableVar.StartLine, declareTableVar.StartColumn,
                    "table variable", $"'{body.VariableName?.Value}' has no table definition to catalog");
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
        var computedExpressions = new Dictionary<string, ScalarExpression>(StringComparer.OrdinalIgnoreCase);
        var computedColumnLines = new Dictionary<string, (int Line, int Column)>(StringComparer.OrdinalIgnoreCase);
        var context = new ColumnBuildContext(defaultCollation, typeAliases, ledger, sourcePath);

        foreach (var columnDefinition in definition.ColumnDefinitions)
        {
            columns.Add(BuildColumn(columnDefinition, context, inlineIndexes, computedExpressions, computedColumnLines));
        }

        columns = ResolveComputedColumnTypes(columns, computedExpressions, computedColumnLines, context);

        // Table-level INDEX (...) definitions - e.g. WWI's `INDEX [IX_...] ([Col])` inside a
        // CREATE TYPE ... AS TABLE - live in TableDefinition.Indexes, a collection entirely
        // separate from TableConstraints (PK/UNIQUE) and a column's own inline .Index. Found
        // while wiring up table-valued parameters (coverage-remediation-plan.md Phase 3.2) but
        // not specific to table types - this was never read for an ordinary CREATE TABLE either,
        // so a real index declared this way was silently invisible to Indexed lookups everywhere.
        inlineIndexes.AddRange(definition.Indexes.Select(i => BuildInlineIndex(i, columnName: string.Empty)));

        return (columns, inlineIndexes);
    }

    /// <summary>Bundles the fixed inputs BuildColumn/ResolveComputedColumnTypes both need but never vary per-column (S107: keeps their own parameter lists to just what varies per call).</summary>
    private readonly record struct ColumnBuildContext(Collation? DefaultCollation, IReadOnlyDictionary<string, SqlType>? TypeAliases, SkipLedger? Ledger, string? SourcePath);

    private static CatalogColumn BuildColumn(
        ColumnDefinition columnDefinition, ColumnBuildContext context,
        List<CatalogIndex> inlineIndexes, Dictionary<string, ScalarExpression> computedExpressions, Dictionary<string, (int Line, int Column)> computedColumnLines)
    {
        var name = columnDefinition.ColumnIdentifier.Value;
        var isNullable = BuildColumnConstraints(columnDefinition, name, inlineIndexes);

        if (columnDefinition.Index is { } inlineIndex)
        {
            inlineIndexes.Add(BuildInlineIndex(inlineIndex, name));
        }

        var declaredType = columnDefinition.DataType;
        var resolvedType = declaredType is null ? null : SqlTypeReferenceResolver.Resolve(declaredType, columnDefinition.Collation, context.TypeAliases);
        if (resolvedType is null && context.SourcePath is not null && declaredType is not null)
        {
            // A table type (TVP shape), CLR UDT, or other explicitly-declared type this
            // resolver declines to guess at (coverage-remediation-plan.md Phase 0.2) - the
            // column still enters the catalog as Unknown (VerdictClassifier already treats
            // a null Type as Unknown, never a guess), but this counts how often it happens
            // so it isn't invisible in the study's coverage accounting. A computed column
            // has null DataType (its type comes from the expression, handled separately
            // below) and never reaches this branch at all.
            context.Ledger?.Record(
                AnalysisPass.Catalog, context.SourcePath, columnDefinition.StartLine, columnDefinition.StartColumn,
                "column type", $"column '{name}' has type '{SchemaObjectNameHelper.Qualify(declaredType.Name)}' which could not be resolved");
        }

        if (resolvedType is { IsStringFamily: true, Collation: null } && context.DefaultCollation is not null)
        {
            // CLAUDE.md Pass 1: "database default collation from any CREATE DATABASE/
            // manifest hint" - applied only when the column itself carries no explicit
            // COLLATE, which always wins.
            resolvedType = resolvedType with { Collation = context.DefaultCollation };
        }

        if (columnDefinition.ComputedColumnExpression is { } computedExpression)
        {
            computedExpressions[name] = computedExpression;
            computedColumnLines[name] = (columnDefinition.StartLine, columnDefinition.StartColumn);
        }

        return new CatalogColumn(
            name,
            resolvedType,
            isNullable,
            IsIdentity: columnDefinition.IdentityOptions is not null,
            IsComputed: columnDefinition.ComputedColumnExpression is not null,
            IsPersisted: columnDefinition.IsPersisted);
    }

    /// <summary>Infers computed columns' types (<see cref="ComputedColumnTypeResolver"/>), applies the default-collation fallback to any newly-resolved string type, and ledgers whichever computed columns are still untyped afterward - the same honesty policy <see cref="BuildColumn"/> applies to an unresolvable declared type.</summary>
    private static List<CatalogColumn> ResolveComputedColumnTypes(
        List<CatalogColumn> columns, Dictionary<string, ScalarExpression> computedExpressions, Dictionary<string, (int Line, int Column)> computedColumnLines, ColumnBuildContext context)
    {
        columns = ComputedColumnTypeResolver.ResolveAll(columns, computedExpressions, context.TypeAliases);
        columns = [.. columns.Select(c => c.Type is { IsStringFamily: true, Collation: null } && context.DefaultCollation is not null
            ? c with { Type = c.Type with { Collation = context.DefaultCollation } }
            : c)];

        if (context.SourcePath is null)
        {
            return columns;
        }

        foreach (var name in computedExpressions.Keys)
        {
            if (columns.Single(c => c.Name == name).Type is not null)
            {
                continue;
            }

            var (line, column) = computedColumnLines[name];
            context.Ledger?.Record(
                AnalysisPass.Catalog, context.SourcePath, line, column,
                "computed column type", $"column '{name}' is computed but its expression type could not be inferred");
        }

        return columns;
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

    private static bool IsColumnstoreIndexType(IndexType? indexType) =>
        indexType?.IndexTypeKind is IndexTypeKind.ClusteredColumnStore or IndexTypeKind.NonClusteredColumnStore;

    /// <summary>
    /// Table-level (<c>INDEX ix (col) WHERE ...</c>/<c>INDEX ix CLUSTERED COLUMNSTORE</c>) and
    /// column-level (<c>col INT INDEX ix WHERE ...</c>) inline index definitions share this same
    /// ScriptDom node, which carries <see cref="IndexDefinition.FilterPredicate"/> and
    /// <see cref="IndexDefinition.IndexType"/> exactly like the standalone <c>CREATE INDEX</c>/
    /// <c>CREATE COLUMNSTORE INDEX</c> paths read (see VisitCreateIndex/VisitCreateColumnStoreIndex
    /// above) - before this fix, this method dropped both flags, so a filtered or columnstore
    /// index declared inline reported <c>IsFiltered</c>/<c>IsColumnstore</c> as false and passed
    /// <see cref="CatalogTable.IsIndexedColumn"/>'s check as an ordinary seekable index, a false
    /// "Indexed=true" for the ranking claim this tool leads with.
    /// </summary>
    private static CatalogIndex BuildInlineIndex(IndexDefinition inlineIndex, string columnName) => new(
        inlineIndex.Name?.Value,
        CatalogIndexKind.Index,
        inlineIndex.Unique,
        inlineIndex.Columns.Count > 0 ? [.. inlineIndex.Columns.Select(ColumnName)] : [columnName],
        [.. inlineIndex.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)],
        IsFiltered: inlineIndex.FilterPredicate is not null,
        IsColumnstore: IsColumnstoreIndexType(inlineIndex.IndexType));

    private static string ColumnName(ColumnWithSortOrder columnWithSortOrder) =>
        columnWithSortOrder.Column.MultiPartIdentifier.Identifiers[^1].Value;
}
