using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Catalog;

public sealed class CatalogBuilderTests
{
    private static DatabaseCatalog BuildFrom(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CatalogBuilder.Build([result]);
    }

    [Fact]
    public void Build_CreateTable_CapturesColumnsTypesAndNullability()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders
            (
                OrderId   INT          NOT NULL PRIMARY KEY,
                OrderCode VARCHAR(20)  NOT NULL,
                Notes     NVARCHAR(MAX) NULL
            );
            """);

        var table = catalog.Find("dbo.Orders");
        Assert.NotNull(table);
        Assert.Equal(CatalogTableKind.Table, table.Kind);
        Assert.Equal(3, table.Columns.Count);

        var orderId = table.FindColumn("OrderId")!;
        Assert.Equal(SqlTypeCategory.Int, orderId.Type!.Category);
        Assert.False(orderId.IsNullable);

        var orderCode = table.FindColumn("OrderCode")!;
        Assert.Equal(SqlTypeCategory.VarChar, orderCode.Type!.Category);
        Assert.Equal(20, orderCode.Type.Length);
        Assert.False(orderCode.IsNullable);

        var notes = table.FindColumn("Notes")!;
        Assert.Equal(SqlTypeCategory.NVarChar, notes.Type!.Category);
        Assert.True(notes.Type.IsMax);
        Assert.True(notes.IsNullable);
    }

    [Fact]
    public void Build_ColumnLevelPrimaryKey_IsIndexed()
    {
        var catalog = BuildFrom("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Name VARCHAR(50) NULL);");

        var table = catalog.Find("dbo.T")!;

        Assert.True(table.IsIndexedColumn("Id"));
        Assert.False(table.IsIndexedColumn("Name"));
    }

    [Fact]
    public void Build_TableLevelCompositePrimaryKey_IndexesBothColumns()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.OrderLines
            (
                OrderId INT NOT NULL,
                LineNumber INT NOT NULL,
                CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber)
            );
            """);

        var table = catalog.Find("dbo.OrderLines")!;
        var pk = table.Indexes.Single(i => i.Kind == CatalogIndexKind.PrimaryKey);

        Assert.Equal(["OrderId", "LineNumber"], pk.KeyColumns);
    }

    [Fact]
    public void Build_CompositeIndex_OnlyLeadingKeyColumnCountsAsIndexed()
    {
        // Regression: a non-leading key column of a composite index cannot drive an index
        // seek on its own - IsIndexedColumn must match IndexDeploymentChecker's
        // key_ordinal = 1 requirement (the oracle's own precondition for confirming a
        // ScanForced/RangeSeek verdict), not just "is a key column somewhere in some index".
        var catalog = BuildFrom("""
            CREATE TABLE dbo.OrderLines
            (
                OrderId INT NOT NULL,
                Amount DECIMAL(9,2) NOT NULL,
                CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, Amount)
            );
            """);

        var table = catalog.Find("dbo.OrderLines")!;

        Assert.True(table.IsIndexedColumn("OrderId"));
        Assert.False(table.IsIndexedColumn("Amount"));
    }

    [Fact]
    public void Build_StandaloneCreateIndex_AttachesToExistingTable()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, OrderCode VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            """);

        var table = catalog.Find("dbo.Orders")!;

        Assert.True(table.IsIndexedColumn("OrderCode"));
        Assert.False(table.IsIndexedColumn("OrderId"));
    }

    [Fact]
    public void Build_AlterTableAdd_MergesIntoExistingTable()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            ALTER TABLE dbo.Orders ADD OrderCode VARCHAR(20) NOT NULL;
            """);

        var table = catalog.Find("dbo.Orders")!;

        Assert.Equal(2, table.Columns.Count);
        Assert.NotNull(table.FindColumn("OrderCode"));
    }

    [Fact]
    public void Build_TempTable_HasNoSchemaAndIsFlaggedTemporary()
    {
        var catalog = BuildFrom("CREATE TABLE #Staging (Id INT NOT NULL);");

        var table = catalog.Find("#Staging")!;

        Assert.Equal(CatalogTableKind.TemporaryTable, table.Kind);
        Assert.Null(table.SchemaName);
    }

    [Fact]
    public void Build_TableVariable_IsCapturedWithColumns()
    {
        var catalog = BuildFrom("DECLARE @t TABLE (Id INT NOT NULL, Name VARCHAR(50) NULL);");

        var table = catalog.Find("@t")!;

        Assert.Equal(CatalogTableKind.TableVariable, table.Kind);
        Assert.Equal(2, table.Columns.Count);
    }

    [Fact]
    public void Build_ColumnWithExplicitCollation_IsCaptured()
    {
        var catalog = BuildFrom(
            "CREATE TABLE dbo.T (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);");

        var column = catalog.Find("dbo.T")!.FindColumn("Code")!;

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", column.Type!.Collation!.Name);
        Assert.True(column.Type.Collation.IsSqlFamily);
    }

    [Fact]
    public void Build_ComputedColumn_IsFlaggedComputedAndPersisted()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Price    DECIMAL(18,2) NOT NULL,
                Quantity INT NOT NULL,
                Total    AS (Price * Quantity) PERSISTED
            );
            """);

        var total = catalog.Find("dbo.T")!.FindColumn("Total")!;

        Assert.True(total.IsComputed);
        Assert.True(total.IsPersisted);

        // Price(decimal) * Quantity(int): different categories, decimal has the higher
        // precedence ordinal (SqlTypeCategory.Decimal > SqlTypeCategory.Int) so it wins -
        // proves ComputedColumnTypeResolver.Combine picks the higher-precedence category
        // rather than leaving a numeric computed column permanently untyped.
        Assert.Equal(SqlTypeCategory.Decimal, total.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnStringConcatenation_InfersType()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.People
            (
                FirstName VARCHAR(40) NOT NULL,
                LastName  VARCHAR(40) NOT NULL,
                FullName  AS (FirstName + ' ' + LastName)
            );
            """);

        var fullName = catalog.Find("dbo.People")!.FindColumn("FullName")!;

        Assert.Equal(SqlTypeCategory.VarChar, fullName.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnStringConcatenation_NvarcharSiblingWinsOverVarchar()
    {
        // Data type precedence, not "whichever comes first": NVarChar has the higher ordinal,
        // so a varchar + nvarchar concatenation must resolve NVarChar regardless of which side
        // of the + it appears on.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.People
            (
                FirstName VARCHAR(40) NOT NULL,
                LastName  NVARCHAR(40) NOT NULL,
                FullName  AS (FirstName + LastName)
            );
            """);

        var fullName = catalog.Find("dbo.People")!.FindColumn("FullName")!;

        Assert.Equal(SqlTypeCategory.NVarChar, fullName.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnCastExpression_InfersTargetType()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Code VARCHAR(10) NOT NULL,
                CodeAsInt AS (CAST(Code AS INT))
            );
            """);

        var codeAsInt = catalog.Find("dbo.T")!.FindColumn("CodeAsInt")!;

        Assert.Equal(SqlTypeCategory.Int, codeAsInt.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnReferencingAnotherComputedColumn_ResolvesViaFixedPoint()
    {
        // Total depends on Subtotal, itself computed - declared in an order T-SQL allows
        // regardless of which is defined first. ResolveAll's fixed-point loop must resolve
        // Subtotal before Total can use its type, not give up after a single pass.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders
            (
                Price    DECIMAL(18,2) NOT NULL,
                Quantity INT NOT NULL,
                Subtotal AS (Price * Quantity),
                Total    AS (Subtotal * 2)
            );
            """);

        var total = catalog.Find("dbo.Orders")!.FindColumn("Total")!;

        Assert.Equal(SqlTypeCategory.Decimal, total.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnWithUnresolvableExpression_StaysUnknownAndLedgered()
    {
        // A scalar UDF call inside a computed column expression is a deliberately unresolved
        // case: the UDF return-type registry isn't built yet at this point in CatalogBuilder's
        // pass ordering (unlike predicates/lineage, which run after the catalog is complete) - it
        // must stay Unknown AND reach the skip ledger, never silently vanish with no trace.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Created DATETIME NOT NULL,
                CreatedLabel AS (dbo.fn_FormatDate(Created))
            );
            """);

        var createdLabel = catalog.Find("dbo.T")!.FindColumn("CreatedLabel")!;

        Assert.Null(createdLabel.Type);
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "computed column type" && e.Reason.Contains("CreatedLabel", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ComputedColumnWithBuiltinFixedReturnTypeFunction_InfersType()
    {
        // YEAR() is a curated fixed-return-type builtin (BuiltinFunctionTypeResolver) - a
        // computed column built from it now types identically to the same call appearing in a
        // predicate or a view's SELECT list, closing the asymmetry ComputedColumnTypeResolver
        // previously had against ScalarExpressionResolver/TypedPredicateExtractor.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Created DATETIME NOT NULL,
                CreatedYear AS (YEAR(Created))
            );
            """);

        var createdYear = catalog.Find("dbo.T")!.FindColumn("CreatedYear")!;

        Assert.NotNull(createdYear.Type);
        Assert.Equal(SqlTypeCategory.Int, createdYear.Type!.Category);
    }

    [Fact]
    public void Build_ComputedColumnWithBuiltinFirstArgumentTypeFunction_InfersSiblingColumnType()
    {
        // ISNULL(Price, 0) takes its first argument's own type - proves the recursive Resolve
        // call inside ComputedColumnTypeResolver.ResolveFunctionCall correctly re-enters the
        // full expression-typing pipeline for the argument, not just a bare-leaf lookup.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Price DECIMAL(10, 2) NOT NULL,
                SafePrice AS (ISNULL(Price, 0))
            );
            """);

        var safePrice = catalog.Find("dbo.T")!.FindColumn("SafePrice")!;

        Assert.NotNull(safePrice.Type);
        Assert.Equal(SqlTypeCategory.Decimal, safePrice.Type!.Category);
    }

    [Fact]
    public void Build_IdentityColumn_IsFlagged()
    {
        var catalog = BuildFrom("CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY);");

        var id = catalog.Find("dbo.T")!.FindColumn("Id")!;

        Assert.True(id.IsIdentity);
    }

    [Fact]
    public void Build_ColumnWithExplicitCollate_NeverOverriddenByDefault()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE DATABASE Foo COLLATE SQL_Latin1_General_CP1_CI_AS;
                CREATE TABLE dbo.T (Col VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
                """)]);

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Equal("Latin1_General_CI_AS", col.Type!.Collation!.Name);
        Assert.Equal(CollationSource.ColumnExplicit, col.Type.Collation.Source);
    }

    [Fact]
    public void Build_CreateDatabaseCollate_FallsBackOntoUncollatedStringColumns()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE DATABASE Foo COLLATE SQL_Latin1_General_CP1_CI_AS;
                CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);
                """)]);

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", col.Type!.Collation!.Name);
        Assert.Equal(CollationSource.DatabaseDefaultFromDdl, col.Type.Collation.Source);
    }

    [Fact]
    public void Build_AlterDatabaseCollate_FallsBackOntoUncollatedStringColumns()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                ALTER DATABASE CURRENT COLLATE Latin1_General_CI_AS;
                CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);
                """)]);

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Equal("Latin1_General_CI_AS", col.Type!.Collation!.Name);
        Assert.Equal(CollationSource.DatabaseDefaultFromDdl, col.Type.Collation.Source);
    }

    [Fact]
    public void Build_ExplicitDatabaseCollation_TakesPrecedenceOverManifestHint()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE DATABASE Foo COLLATE SQL_Latin1_General_CP1_CI_AS;
                CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);
                """)],
            manifestDeclaredCollation: "Latin1_General_CI_AS");

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", col.Type!.Collation!.Name);
        Assert.Equal(CollationSource.DatabaseDefaultFromDdl, col.Type.Collation.Source);
    }

    [Fact]
    public void Build_NoExplicitDatabaseCollation_FallsBackToManifestHint()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);")],
            manifestDeclaredCollation: "Latin1_General_CI_AS");

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Equal("Latin1_General_CI_AS", col.Type!.Collation!.Name);
        Assert.Equal(CollationSource.DatabaseDefaultFromManifest, col.Type.Collation.Source);
    }

    [Fact]
    public void Build_TempTableColumn_DefaultsToManifestTempdbCollationNotDatabaseCollation()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE #Staging (Col VARCHAR(20) NOT NULL);")],
            manifestDeclaredCollation: "Latin1_General_CI_AS",
            manifestTempdbCollation: "SQL_Latin1_General_CP1_CI_AS");

        var col = catalog.Find("#Staging")!.FindColumn("Col")!;

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", col.Type!.Collation!.Name);
    }

    [Fact]
    public void Build_TableVariableColumn_DefaultsToManifestTempdbCollationNotDatabaseCollation()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE PROCEDURE dbo.UsesTableVar AS
                BEGIN
                    DECLARE @t TABLE (Col VARCHAR(20) NOT NULL);
                    SELECT 1;
                END
                """)],
            manifestDeclaredCollation: "Latin1_General_CI_AS",
            manifestTempdbCollation: "SQL_Latin1_General_CP1_CI_AS");

        var tableVar = catalog.Find("@t", "dbo.UsesTableVar")!;

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", tableVar.FindColumn("Col")!.Type!.Collation!.Name);
    }

    [Fact]
    public void Build_NoManifestTempdbCollation_TempTableFallsBackToDatabaseCollation()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE #Staging (Col VARCHAR(20) NOT NULL);")],
            manifestDeclaredCollation: "Latin1_General_CI_AS");

        Assert.Equal("Latin1_General_CI_AS", catalog.Find("#Staging")!.FindColumn("Col")!.Type!.Collation!.Name);
    }

    [Fact]
    public void Build_CaseSensitiveDeclaredCollation_LedgersAnHonestWarning()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);")],
            manifestDeclaredCollation: "Latin1_General_CS_AS");

        Assert.Contains(
            catalog.Skipped.Entries,
            e => e.ConstructKind == "case-sensitive collation" && e.Reason.Contains("Latin1_General_CS_AS", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CaseInsensitiveDeclaredCollation_NoCaseSensitivityWarning()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);")],
            manifestDeclaredCollation: "Latin1_General_CI_AS");

        Assert.DoesNotContain(catalog.Skipped.Entries, e => e.ConstructKind == "case-sensitive collation");
    }

    [Fact]
    public void Build_NoDatabaseCollationAnySource_ColumnCollationStaysNull()
    {
        var catalog = CatalogBuilder.Build([Parse("CREATE TABLE dbo.T (Col VARCHAR(20) NOT NULL);")]);

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Null(col.Type!.Collation);
    }

    [Fact]
    public void Build_DatabaseCollation_DoesNotApplyToNonStringColumns()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE DATABASE Foo COLLATE SQL_Latin1_General_CP1_CI_AS;
                CREATE TABLE dbo.T (Col INT NOT NULL);
                """)]);

        var col = catalog.Find("dbo.T")!.FindColumn("Col")!;

        Assert.Null(col.Type!.Collation);
    }

    [Fact]
    public void Build_AlterColumnAfterCreateTable_YieldsPostAlterType()
    {
        // docs/audit-remediation-plan.md Phase 2.5: the exact pattern this tool exists to catch
        // - a migration script widening a column's type. Before the fix, the ORIGINAL type
        // stayed in the catalog forever, producing wrong-direction findings on precisely this.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);
                ALTER TABLE dbo.Users ALTER COLUMN DisplayName NVARCHAR(40) NOT NULL;
                """)]);

        var column = catalog.Find("dbo.Users")!.FindColumn("DisplayName")!;

        Assert.Equal(SqlTypeCategory.NVarChar, column.Type!.Category);
    }

    [Fact]
    public void Build_AlterColumnAcrossFiles_AppliesRegardlessOfFileOrder()
    {
        var catalog = CatalogBuilder.Build(
        [
            Parse("ALTER TABLE dbo.Users ALTER COLUMN DisplayName NVARCHAR(40) NOT NULL;"),
            Parse("CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);"),
        ]);

        var column = catalog.Find("dbo.Users")!.FindColumn("DisplayName")!;

        Assert.Equal(SqlTypeCategory.NVarChar, column.Type!.Category);
        Assert.Empty(catalog.Skipped.Entries);
    }

    [Fact]
    public void Build_AlterColumnUnresolvableTargetType_NullsColumnRatherThanKeepingStaleType()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Users (DisplayName VARCHAR(40) NOT NULL);
                ALTER TABLE dbo.Users ALTER COLUMN DisplayName dbo.SomeUserDefinedType NOT NULL;
                """)]);

        var column = catalog.Find("dbo.Users")!.FindColumn("DisplayName")!;

        Assert.Null(column.Type);
    }

    [Fact]
    public void Build_ColumnDeclaredWithSysname_ResolvesToNVarChar128()
    {
        // docs/audit-remediation-plan.md Phase 6.2: sysname is pervasive in admin-script repos
        // for object/schema names.
        var catalog = BuildFrom("CREATE TABLE dbo.Objects (ObjectName sysname NOT NULL);");

        var column = catalog.Find("dbo.Objects")!.FindColumn("ObjectName")!;

        Assert.Equal(SqlTypeCategory.NVarChar, column.Type!.Category);
        Assert.Equal(128, column.Type.Length);
    }

    [Fact]
    public void Build_ColumnDeclaredWithCatalogedTypeAlias_ResolvesThroughToUnderlyingType()
    {
        var catalog = BuildFrom("""
            CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;
            CREATE TABLE dbo.Orders (OrderId dbo.MyIntAlias NOT NULL);
            """);

        var column = catalog.Find("dbo.Orders")!.FindColumn("OrderId")!;

        Assert.Equal(SqlTypeCategory.Int, column.Type!.Category);
    }

    [Fact]
    public void Build_TypeAliasDeclaredInLaterFile_StillResolvesForEarlierFilesTable()
    {
        // Same cross-file-ordering guarantee Build_AlterColumnAcrossFiles_
        // AppliesRegardlessOfFileOrder locks in for ALTER COLUMN - a repo's type-alias file
        // routinely sorts after the tables that use it.
        var catalog = CatalogBuilder.Build(
        [
            Parse("CREATE TABLE dbo.Orders (OrderId dbo.MyIntAlias NOT NULL);"),
            Parse("CREATE TYPE dbo.MyIntAlias FROM INT NOT NULL;"),
        ]);

        var column = catalog.Find("dbo.Orders")!.FindColumn("OrderId")!;

        Assert.Equal(SqlTypeCategory.Int, column.Type!.Category);
    }

    [Fact]
    public void Build_ColumnDeclaredWithUnknownUserType_StaysUnresolved()
    {
        var catalog = BuildFrom("CREATE TABLE dbo.Orders (OrderId dbo.NotARealAlias NOT NULL);");

        var column = catalog.Find("dbo.Orders")!.FindColumn("OrderId")!;

        Assert.Null(column.Type);
    }

    [Fact]
    public void Build_CreateTypeAliasWithUnresolvableUnderlyingType_RecordsSkip()
    {
        var catalog = BuildFrom("CREATE TYPE dbo.BadAlias FROM dbo.SomeOtherUnknownType;");

        Assert.Empty(catalog.TypeAliases);
        Assert.Contains(catalog.Skipped.Entries, s => s.ConstructKind == "CREATE TYPE ... FROM");
    }

    [Fact]
    public void Build_DropColumn_RemovesColumnFromCatalog()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Users (Id INT NOT NULL, Obsolete VARCHAR(10) NULL);
                ALTER TABLE dbo.Users DROP COLUMN Obsolete;
                """)]);

        var table = catalog.Find("dbo.Users")!;

        Assert.Null(table.FindColumn("Obsolete"));
        Assert.NotNull(table.FindColumn("Id"));
    }

    [Fact]
    public void Build_AlterTableAddOnScopedTempTable_UpdatesTheScopedEntryNotAnUnscopedCopy()
    {
        // The same "scoped entry, unscoped lookup" bug class as the predicate-side index lookup
        // fix (coverage-remediation-plan.md Phase 3.2): ALTER TABLE ADD on a #temp table used an
        // unscoped Find (silently missing the scoped entry, treating it as an unresolved target)
        // and an unscoped AddOrReplace on write-back (which would have created a stray unscoped
        // duplicate instead of updating the real scoped one).
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE PROCEDURE dbo.usp_Test
                AS
                BEGIN
                    CREATE TABLE #t (Col1 INT NOT NULL);
                    ALTER TABLE #t ADD Col2 VARCHAR(20) NOT NULL;
                END
                """)]);

        var scoped = catalog.Find("#t", "dbo.usp_Test");

        Assert.NotNull(scoped);
        Assert.NotNull(scoped!.FindColumn("Col2"));
        Assert.Null(catalog.Find("#t"));
    }

    [Fact]
    public void Build_TempTablesInsideDifferentProcedures_SameNameDifferentShape_DoNotClobberEachOther()
    {
        // docs/audit-remediation-plan.md Phase 2.5 "Done when": two procedures with same-named
        // temp tables of different shapes each resolve correctly.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE PROCEDURE dbo.usp_First
                AS
                BEGIN
                    CREATE TABLE #t (Col INT NOT NULL);
                END
                GO
                CREATE PROCEDURE dbo.usp_Second
                AS
                BEGIN
                    CREATE TABLE #t (Col VARCHAR(20) NOT NULL);
                END
                """)]);

        var firstTemp = catalog.Find("#t", "dbo.usp_First")!;
        var secondTemp = catalog.Find("#t", "dbo.usp_Second")!;

        Assert.Equal(SqlTypeCategory.Int, firstTemp.FindColumn("Col")!.Type!.Category);
        Assert.Equal(SqlTypeCategory.VarChar, secondTemp.FindColumn("Col")!.Type!.Category);
    }

    [Fact]
    public void Build_CreateOrAlterTriggerBody_TempTableScopedToTrigger()
    {
        // CreateOrAlterTriggerStatement is a distinct ScriptDOM node type from
        // CreateTriggerStatement/AlterTriggerStatement - procedures and functions already got
        // all three variants; triggers didn't (coverage-remediation-plan.md Phase 2.1).
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL);
                GO
                CREATE OR ALTER TRIGGER dbo.trg_Orders ON dbo.Orders
                AFTER INSERT
                AS
                BEGIN
                    CREATE TABLE #t (Col INT NOT NULL);
                END
                """)]);

        var temp = catalog.Find("#t", "dbo.trg_Orders");

        Assert.NotNull(temp);
        Assert.Null(catalog.Find("#t"));
    }

    [Fact]
    public void Build_TableVariablesInsideDifferentProcedures_SameNameDifferentShape_DoNotClobberEachOther()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE PROCEDURE dbo.usp_First
                AS
                BEGIN
                    DECLARE @t TABLE (Col INT NOT NULL);
                END
                GO
                CREATE PROCEDURE dbo.usp_Second
                AS
                BEGIN
                    DECLARE @t TABLE (Col VARCHAR(20) NOT NULL);
                END
                """)]);

        var firstVar = catalog.Find("@t", "dbo.usp_First")!;
        var secondVar = catalog.Find("@t", "dbo.usp_Second")!;

        Assert.Equal(SqlTypeCategory.Int, firstVar.FindColumn("Col")!.Type!.Category);
        Assert.Equal(SqlTypeCategory.VarChar, secondVar.FindColumn("Col")!.Type!.Category);
    }

    [Fact]
    public void Find_TableVariable_ScopeMiss_NeverFallsBackToAnUnrelatedScopesDeclaration()
    {
        // The gap this fix closes: DatabaseCatalog.Find(name, scope)'s unscoped fallback applied
        // to table variables too, even though a table variable is strictly proc-local in real
        // SQL Server (FromScopeResolver's own doc comment) - a scope MISS (querying from a proc
        // that never declared its own @t) used to silently match a DIFFERENT proc's @t instead of
        // staying unresolved.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE PROCEDURE dbo.usp_Declares
                AS
                BEGIN
                    DECLARE @t TABLE (Col INT NOT NULL);
                END
                GO
                CREATE PROCEDURE dbo.usp_NeverDeclares
                AS
                BEGIN
                    SELECT 1;
                END
                """)]);

        Assert.Null(catalog.Find("@t", "dbo.usp_NeverDeclares"));
    }

    [Fact]
    public void Build_MultiStatementTvfReturnVariable_CatalogedUnderFunctionScope()
    {
        // RETURNS @t TABLE(...) is a DeclareTableVariableBody hanging off the return type, not a
        // DeclareTableVariableStatement, so this was never registered before (coverage-
        // remediation-plan.md Phase 3.4) - unlike an ordinary body-declared table variable.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE FUNCTION dbo.fn_GetCodes()
                RETURNS @t TABLE (Code VARCHAR(20) NOT NULL)
                AS
                BEGIN
                    RETURN;
                END
                """)]);

        var returnVar = catalog.Find("@t", "dbo.fn_GetCodes");

        Assert.NotNull(returnVar);
        Assert.Equal(SqlTypeCategory.VarChar, returnVar!.FindColumn("Code")!.Type!.Category);
        Assert.Null(catalog.Find("@t"));
    }

    [Fact]
    public void Build_CreateTypeAsTable_RegistersColumnShapeIncludingInlineIndex()
    {
        // CREATE TYPE ... AS TABLE has no visitor anywhere - WWI's manifest lists a User Defined
        // Types path with four such files consumed as TVPs by four procs (coverage-remediation-
        // plan.md Phase 3.2).
        var catalog = BuildFrom("""
            CREATE TYPE Website.OrderLineList AS TABLE
            (
                StockItemID INT NOT NULL,
                Quantity INT NOT NULL,
                INDEX IX_OrderLineList (StockItemID)
            );
            """);

        var tableType = catalog.Find("Website.OrderLineList");

        Assert.NotNull(tableType);
        Assert.Equal(CatalogTableKind.TableType, tableType!.Kind);
        Assert.Equal(SqlTypeCategory.Int, tableType.FindColumn("StockItemID")!.Type!.Category);
        Assert.True(tableType.IsIndexedColumn("StockItemID"));
    }

    [Fact]
    public void Build_TableValuedParameter_RegisteredUnderProcedureScopeWithSameShape()
    {
        var catalog = BuildFrom(
            """
            CREATE TYPE Website.OrderLineList AS TABLE (StockItemID INT NOT NULL);
            GO
            CREATE PROCEDURE Website.InsertOrderLines
                @OrderLines Website.OrderLineList READONLY
            AS
            BEGIN
                RETURN;
            END
            """);

        var parameterRelation = catalog.Find("@OrderLines", "Website.InsertOrderLines");

        Assert.NotNull(parameterRelation);
        Assert.Equal(CatalogTableKind.TableVariable, parameterRelation!.Kind);
        Assert.Equal(SqlTypeCategory.Int, parameterRelation.FindColumn("StockItemID")!.Type!.Category);
        Assert.Null(catalog.Find("@OrderLines"));
    }

    [Fact]
    public void Build_ScalarParameter_NotRegisteredAsTableValued()
    {
        // A parameter whose type isn't a registered table type (an ordinary scalar type) must
        // not be mistaken for a TVP.
        var catalog = BuildFrom(
            """
            CREATE PROCEDURE dbo.usp_Ordinary @Id INT
            AS
            BEGIN
                RETURN;
            END
            """);

        Assert.Null(catalog.Find("@Id", "dbo.usp_Ordinary"));
    }

    [Fact]
    public void Build_ClrTableValuedFunction_NoVariableNameToRegister_DoesNotThrow()
    {
        // A CLR TVF's RETURNS TABLE(...) has the same TableValuedFunctionReturnType shape as a
        // multi-statement TVF's RETURNS @t TABLE(...), but no @variable name at all - reproduced
        // as a real NullReferenceException while adding the MSTVF return-variable registration
        // above (coverage-remediation-plan.md Phase 3.4), from body.VariableName.Value on a null
        // VariableName.
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE FUNCTION dbo.fn_Clr() RETURNS TABLE (Col INT NOT NULL) AS EXTERNAL NAME MyAssembly.[MyClass].[MyMethod];")]);

        Assert.Null(catalog.Find("@t", "dbo.fn_Clr"));
    }

    [Fact]
    public void Build_CrossDatabaseReference_DoesNotMergeWithSameSchemaTableInCurrentDatabase()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Users (Id INT NOT NULL, DisplayName VARCHAR(40) NOT NULL);
                ALTER TABLE OtherDb.dbo.Users ADD ExtraColumn INT NULL;
                """)]);

        var localUsers = catalog.Find("dbo.Users")!;

        Assert.Null(localUsers.FindColumn("ExtraColumn"));
        Assert.Contains(catalog.Skipped.Entries, e => e.Reason.Contains("OtherDb.dbo.Users", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FilteredIndex_NotCountedAsSeekableForRanking()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL, Status VARCHAR(20) NOT NULL);
                CREATE INDEX IX_Orders_Status ON dbo.Orders(Status) WHERE Status = 'Open';
                """)]);

        Assert.False(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_OrdinaryIndex_StillCountsAsSeekable()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL, Status VARCHAR(20) NOT NULL);
                CREATE INDEX IX_Orders_Status ON dbo.Orders(Status);
                """)]);

        Assert.True(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_TableLevelInlineIndexDefinition_CountsAsSeekable()
    {
        // TableDefinition.Indexes (the INDEX (...) form written inside the column list, e.g.
        // WWI's `INDEX [IX_...] ([Col])`) is a collection entirely separate from
        // TableConstraints and a column's own inline .Index - found while wiring up table-valued
        // parameters (coverage-remediation-plan.md Phase 3.2), but this was never read for an
        // ordinary CREATE TABLE either, not just CREATE TYPE ... AS TABLE.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders
                (
                    Id INT NOT NULL,
                    Status VARCHAR(20) NOT NULL,
                    INDEX IX_Orders_Status (Status)
                );
                """)]);

        Assert.True(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_ColumnstoreIndex_NotCountedAsSeekableForRanking()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL, Status VARCHAR(20) NOT NULL);
                CREATE COLUMNSTORE INDEX IX_Orders_CS ON dbo.Orders(Status);
                """)]);

        Assert.False(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_TableLevelInlineFilteredIndex_NotCountedAsSeekableForRanking()
    {
        // The bug this closes: BuildInlineIndex used to drop FilterPredicate/IndexType entirely,
        // so a table-level inline INDEX(...) WHERE ... reported Indexed=true - a false positive
        // for the ranking claim this tool leads with (ScanForced + indexed + depth >= 1 first).
        // The standalone CREATE INDEX ... WHERE ... path (Build_FilteredIndex_NotCountedAsSeekableForRanking
        // above) already got this right; this is the same construct via table-level inline syntax.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders
                (
                    Id INT NOT NULL,
                    Status VARCHAR(20) NOT NULL,
                    INDEX IX_Orders_Status (Status) WHERE Status = 'Open'
                );
                """)]);

        Assert.False(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_TableLevelInlineColumnstoreIndex_NotCountedAsSeekableForRanking()
    {
        // Same bug as the filtered case above, for the columnstore flag: a table-level inline
        // `INDEX ix CLUSTERED COLUMNSTORE` used to report Indexed=true.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders
                (
                    Id INT NOT NULL,
                    Status VARCHAR(20) NOT NULL,
                    INDEX IX_Orders_CS CLUSTERED COLUMNSTORE
                );
                """)]);

        Assert.False(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_ColumnLevelInlineFilteredIndex_NotCountedAsSeekableForRanking()
    {
        // Same bug, for the column-level inline form (columnDefinition.Index), e.g.
        // `Status VARCHAR(20) INDEX ix WHERE ...` - a distinct code path from the table-level
        // TableDefinition.Indexes collection above (BuildColumn's own inlineIndex branch).
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders
                (
                    Id INT NOT NULL,
                    Status VARCHAR(20) INDEX IX_Orders_Status WHERE Status = 'Open'
                );
                """)]);

        Assert.False(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_ColumnLevelInlineOrdinaryIndex_StillCountsAsSeekable()
    {
        // Near-miss for the two fixes above: a plain (non-filtered, non-columnstore) inline
        // column-level index must keep counting as seekable - the fix must not overcorrect.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders
                (
                    Id INT NOT NULL,
                    Status VARCHAR(20) INDEX IX_Orders_Status
                );
                """)]);

        Assert.True(catalog.Find("dbo.Orders")!.IsIndexedColumn("Status"));
    }

    [Fact]
    public void Build_SelectIntoTempTable_InfersColumnTypesFromSourceTable()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerName VARCHAR(40) NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Snapshot
                AS
                BEGIN
                    SELECT Id, CustomerName AS Name INTO #snapshot FROM dbo.Orders;
                END
                """)]);

        var snapshot = catalog.Find("#snapshot", "dbo.usp_Snapshot")!;

        Assert.Equal(SqlTypeCategory.Int, snapshot.FindColumn("Id")!.Type!.Category);
        Assert.Equal(SqlTypeCategory.VarChar, snapshot.FindColumn("Name")!.Type!.Category);
    }

    [Fact]
    public void Build_SelectIntoFromCteSharingNameWithRealTable_TargetColumnHasNoGuessedType()
    {
        // A CTE is never schema-qualified, so it always shadows a same-named real base table for
        // its own statement's lifetime - resolving against the catalog instead (the previous
        // behavior) would have silently attributed #snapshot.Id's type to the REAL dbo.Orders
        // table's Id column, even though the CTE's own Id here is a different type entirely. The
        // same bug class Phase 1.5 fixed across seven Predicates-layer scanners, present in
        // SelectIntoColumnResolver (Catalog layer) too until this fix - correctly declined here
        // rather than resolved against the wrong table, per CLAUDE.md's pass-ordering rule
        // (catalog-building cannot depend on Lineage-level CTE resolution the way Predicates now
        // does, so the fix here is a name-only decline, not a full resolution upgrade).
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL, CustomerName VARCHAR(40) NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Snapshot
                AS
                BEGIN
                    WITH Orders AS (SELECT CAST(1 AS BIGINT) AS Id)
                    SELECT Id INTO #snapshot FROM Orders;
                END
                """)]);

        var snapshot = catalog.Find("#snapshot", "dbo.usp_Snapshot")!;

        Assert.Null(snapshot.FindColumn("Id")!.Type);
    }

    [Fact]
    public void Build_SelectIntoWithAmbiguousUnaliasedJoinTables_QualifiedReferenceHasNoGuessedType()
    {
        // `FROM dbo.T JOIN audit.T ON ...` - two different tables sharing the same unqualified
        // bare name T, neither aliased - exposes the identical alias ambiguity
        // FromScopeResolver.cs's own poison rule already guards against at the Lineage layer.
        // Silently last-wins (the previous behavior) would attribute Code's type to whichever of
        // the two happened to be flattened last, regardless of which T.Code the query author meant.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.T (Id INT NOT NULL, Code VARCHAR(10) NOT NULL);
                GO
                CREATE TABLE audit.T (Id INT NOT NULL, Code INT NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Snapshot
                AS
                BEGIN
                    SELECT dbo.T.Code INTO #snapshot FROM dbo.T JOIN audit.T ON dbo.T.Id = audit.T.Id;
                END
                """)]);

        var snapshot = catalog.Find("#snapshot", "dbo.usp_Snapshot")!;

        Assert.Null(snapshot.FindColumn("Code")!.Type);
    }

    [Fact]
    public void Build_SelectIntoWithDistinctAliases_StillResolvesQualifiedReference()
    {
        // Control: the same two-table join, but with real, distinct aliases - proves the fix
        // above is scoped to the genuine ambiguity (an unaliased bare-name collision), not to
        // joins in general.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.T (Id INT NOT NULL, Code VARCHAR(10) NOT NULL);
                GO
                CREATE TABLE audit.T (Id INT NOT NULL, Code INT NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Snapshot
                AS
                BEGIN
                    SELECT t1.Code INTO #snapshot FROM dbo.T t1 JOIN audit.T t2 ON t1.Id = t2.Id;
                END
                """)]);

        var snapshot = catalog.Find("#snapshot", "dbo.usp_Snapshot")!;

        Assert.Equal(SqlTypeCategory.VarChar, snapshot.FindColumn("Code")!.Type!.Category);
    }

    [Fact]
    public void Build_SelectIntoWithNonColumnExpression_TargetColumnHasNoGuessedType()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE dbo.Orders (Id INT NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Snapshot
                AS
                BEGIN
                    SELECT Id, COUNT(*) AS Total INTO #snapshot FROM dbo.Orders GROUP BY Id;
                END
                """)]);

        var snapshot = catalog.Find("#snapshot", "dbo.usp_Snapshot")!;

        Assert.Null(snapshot.FindColumn("Total")!.Type);
    }

    [Fact]
    public void Build_InlinePrimaryKeyColumn_IsNotNullableEvenWithoutExplicitNotNull()
    {
        var catalog = CatalogBuilder.Build([Parse("CREATE TABLE dbo.T (Id INT PRIMARY KEY);")]);

        Assert.False(catalog.Find("dbo.T")!.FindColumn("Id")!.IsNullable);
    }

    [Fact]
    public void Build_TableLevelPrimaryKeyConstraint_MarksKeyColumnsNotNullable()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE dbo.T (Id INT, CONSTRAINT PK_T PRIMARY KEY (Id));")]);

        Assert.False(catalog.Find("dbo.T")!.FindColumn("Id")!.IsNullable);
    }

    [Fact]
    public void Build_SpatialColumn_TypeIsNullAndLedgered()
    {
        // sys.geography/geometry are CLR UDTs with no local definition to resolve (coverage-
        // remediation-plan.md Phase 0.2) - the column still enters the catalog (Type: null,
        // which VerdictClassifier already treats as Unknown), but until this pass it was
        // uncounted, indistinguishable from a genuine resolution success.
        var catalog = BuildFrom("CREATE TABLE dbo.Cities (Id INT NOT NULL, Location GEOGRAPHY NULL);");

        Assert.Null(catalog.Find("dbo.Cities")!.FindColumn("Location")!.Type);
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "column type" && e.Reason.Contains("Location", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ComputedColumnWithNoDeclaredType_NotLedgered()
    {
        // A computed column's type comes from its expression, not a DataType node - this must
        // not be confused with the spatial/CLR case above, which has a real declared type that
        // failed to resolve.
        var catalog = BuildFrom("CREATE TABLE dbo.T (A INT NOT NULL, B AS (A + 1));");

        Assert.DoesNotContain(catalog.Skipped.Entries, e => e.ConstructKind == "column type");
    }

    [Fact]
    public void Build_CreateAssembly_Ledgered()
    {
        var catalog = BuildFrom("CREATE ASSEMBLY MyAssembly FROM 0x4D5A;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CLR assembly" && e.Reason.Contains("MyAssembly", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateAggregate_Ledgered()
    {
        var catalog = BuildFrom("CREATE AGGREGATE dbo.Concat(@input NVARCHAR(4000)) RETURNS NVARCHAR(4000) EXTERNAL NAME MyAssembly.[Concat];");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CLR aggregate" && e.Reason.Contains("dbo.Concat", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateTypeExternalName_Ledgered()
    {
        var catalog = BuildFrom("CREATE TYPE dbo.Point EXTERNAL NAME MyAssembly.[Point];");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CLR user-defined type" && e.Reason.Contains("dbo.Point", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateFullTextIndex_Ledgered()
    {
        // ConstructCoverage.json carried this as "Ledgered" (verifiedBy: null) with no code
        // anywhere actually recording it - a phantom claim, since "Ledgered" means every
        // occurrence reaches a SkipLedger entry.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Documents (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NULL);
            GO
            CREATE FULLTEXT INDEX ON dbo.Documents(Body) KEY INDEX PK__Document;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "full-text index" && e.Reason.Contains("dbo.Documents", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AlterFullTextIndex_Ledgered()
    {
        // Found by the reflection backstop (StatementVariantParityTests) the moment
        // CreateFullTextIndexStatement above got its own visitor.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Documents2 (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NULL);
            GO
            CREATE FULLTEXT INDEX ON dbo.Documents2(Body) KEY INDEX PK__Document2;
            GO
            ALTER FULLTEXT INDEX ON dbo.Documents2 ENABLE;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "full-text index" && e.Reason.Contains("ALTER FULLTEXT INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateSpatialIndex_Ledgered()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Locations (Id INT NOT NULL PRIMARY KEY, GeoCol GEOGRAPHY NULL);
            GO
            CREATE SPATIAL INDEX SIdx_Locations ON dbo.Locations(GeoCol) USING GEOGRAPHY_GRID;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "spatial index" && e.Reason.Contains("dbo.Locations", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateXmlIndex_Ledgered()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Configs (Id INT NOT NULL PRIMARY KEY, Payload XML NULL);
            GO
            CREATE PRIMARY XML INDEX PXmlIdx_Configs ON dbo.Configs(Payload);
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "XML index" && e.Reason.Contains("Payload", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateExternalTable_Ledgered()
    {
        var catalog = BuildFrom("""
            CREATE EXTERNAL TABLE dbo.ExternalOrders (Id INT NOT NULL)
            WITH (LOCATION = '/data/orders/', DATA_SOURCE = MyDataSource, FILE_FORMAT = MyFileFormat);
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "external table" && e.Reason.Contains("dbo.ExternalOrders", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AlterAssembly_Ledgered()
    {
        // Found by StatementVariantParityTests' reflection backstop, not manual audit -
        // ALTER ASSEMBLY was silently unhandled (no ledger entry at all) despite CREATE ASSEMBLY
        // already being ledgered (coverage-remediation-plan.md Phase 2.1).
        var catalog = BuildFrom("ALTER ASSEMBLY MyAssembly FROM 0x4D5A;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CLR assembly" && e.Reason.Contains("MyAssembly", StringComparison.Ordinal) && e.Reason.Contains("ALTER ASSEMBLY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AlterIndexDisable_ColumnNoLongerReportsIndexed()
    {
        // Found by the same reflection backstop, later fixed: DISABLE now flips
        // CatalogIndex.IsDisabled by name, so a disabled index genuinely stops counting as
        // seekable rather than silently reporting a disabled index as still seekable.
        var catalog = BuildFrom(
            """
            CREATE TABLE dbo.T (Col INT NOT NULL);
            CREATE INDEX IX_T_Col ON dbo.T(Col);
            ALTER INDEX IX_T_Col ON dbo.T DISABLE;
            """);

        var table = catalog.Find("dbo.T")!;
        Assert.False(table.IsIndexedColumn("Col"));
    }

    [Fact]
    public void Build_AlterIndexRebuild_ReEnablesAPreviouslyDisabledIndex()
    {
        var catalog = BuildFrom(
            """
            CREATE TABLE dbo.T (Col INT NOT NULL);
            CREATE INDEX IX_T_Col ON dbo.T(Col);
            ALTER INDEX IX_T_Col ON dbo.T DISABLE;
            ALTER INDEX IX_T_Col ON dbo.T REBUILD;
            """);

        var table = catalog.Find("dbo.T")!;
        Assert.True(table.IsIndexedColumn("Col"));
    }

    [Fact]
    public void Build_AlterIndexDisableAll_DisablesEveryIndexOnTheTable()
    {
        var catalog = BuildFrom(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL);
            CREATE INDEX IX_T_A ON dbo.T(A);
            CREATE INDEX IX_T_B ON dbo.T(B);
            ALTER INDEX ALL ON dbo.T DISABLE;
            """);

        var table = catalog.Find("dbo.T")!;
        Assert.False(table.IsIndexedColumn("A"));
        Assert.False(table.IsIndexedColumn("B"));
    }

    [Fact]
    public void Build_AlterIndexReorganize_StillLedgeredAsNotAffectingSeekability()
    {
        // REORGANIZE never changes whether an index is usable, so it's ledgered rather than
        // modeled - the real risk this pass guards against is limited to DISABLE/REBUILD.
        var catalog = BuildFrom(
            """
            CREATE TABLE dbo.T (Col INT NOT NULL);
            CREATE INDEX IX_T_Col ON dbo.T(Col);
            ALTER INDEX IX_T_Col ON dbo.T REORGANIZE;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "ALTER INDEX" && e.Reason.Contains("IX_T_Col", StringComparison.Ordinal));

        var table = catalog.Find("dbo.T")!;
        Assert.True(table.IsIndexedColumn("Col"));
    }

    [Fact]
    public void Build_CreateAssembly_DoesNotTripleCountAcrossThreeBuildPhases()
    {
        // CatalogBuilder walks every file three times (CollectTypeAliases/CollectTables/
        // ApplyEverythingElse) - the CLR visitors must be gated to exactly one phase.
        var catalog = BuildFrom("CREATE ASSEMBLY MyAssembly FROM 0x4D5A;");

        Assert.Single(catalog.Skipped.Entries, e => e.ConstructKind == "CLR assembly");
    }

    [Fact]
    public void Build_DropTable_RemovesTableFromCatalog()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            DROP TABLE dbo.Orders;
            """);

        Assert.Null(catalog.Find("dbo.Orders"));
    }

    [Fact]
    public void Build_DropTableThenRecreateWithDifferentShape_KeepsTheRecreatedShape()
    {
        // The false-positive class this pass exists to close: a migration script that drops
        // and rebuilds a table with a DIFFERENT column type must resolve predicates against
        // the recreated shape, never the stale original.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            DROP TABLE dbo.Orders;
            CREATE TABLE dbo.Orders (OrderCode NVARCHAR(20) NOT NULL);
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.Equal(SqlTypeCategory.NVarChar, table.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public void Build_DropTableNeverCataloged_RecordsLedgerEntry()
    {
        var catalog = BuildFrom("DROP TABLE dbo.NeverSeen;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "DROP TABLE" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DropIndex_TableColumnNoLongerCountsAsIndexed()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            DROP INDEX IX_Orders_OrderCode ON dbo.Orders;
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.False(table.IsIndexedColumn("OrderCode"));
    }

    [Fact]
    public void Build_DropFunction_ScalarUdfReturnTypeNoLongerRegistered()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_Code() RETURNS NVARCHAR(20) AS BEGIN RETURN N'x'; END
            GO
            DROP FUNCTION dbo.fn_Code;
            """);

        Assert.False(catalog.TryGetScalarFunctionReturnType("dbo.fn_Code", out _));
    }

    [Fact]
    public void Build_TruncateTable_DoesNotChangeCatalog()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            TRUNCATE TABLE dbo.Orders;
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.Equal(SqlTypeCategory.VarChar, table.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public void Build_CreateTableWithNoInlineDefinition_LedgersRatherThanSilentlyDropping()
    {
        // CREATE TABLE ... AS EDGE (a SQL Server graph edge table) has no inline column list -
        // its columns are implicit - the same "no Definition" shape a CTAS-only form would
        // have. Ledgered rather than silently skipped.
        var catalog = BuildFrom("CREATE TABLE dbo.Likes AS EDGE;");

        Assert.Null(catalog.Find("dbo.Likes"));
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CREATE TABLE" && e.Reason.Contains("Likes", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DropProcedure_DoesNotThrowAndLeavesUnrelatedCatalogDataIntact()
    {
        // Procedures are never registered by name in any catalog structure other passes
        // consult, so DROP PROCEDURE has nothing to remove - this just confirms the statement
        // is walked without throwing and without side effects on real catalog data.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_GetOrders AS SELECT OrderCode FROM dbo.Orders;
            GO
            DROP PROCEDURE dbo.usp_GetOrders;
            """);

        Assert.NotNull(catalog.Find("dbo.Orders"));
    }

    [Fact]
    public void Build_DropTrigger_DoesNotThrowAndLeavesUnrelatedCatalogDataIntact()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            GO
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS SELECT 1;
            GO
            DROP TRIGGER dbo.trg_Orders;
            """);

        Assert.NotNull(catalog.Find("dbo.Orders"));
    }

    [Fact]
    public void Build_SpRename_ObjectForm_RenamesTable()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            EXEC sp_rename 'dbo.Orders', 'PurchaseOrders';
            """);

        Assert.Null(catalog.Find("dbo.Orders"));
        var table = catalog.Find("dbo.PurchaseOrders")!;
        Assert.Equal(SqlTypeCategory.VarChar, table.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public void Build_SpRename_ColumnForm_RenamesColumnInPlace()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            EXEC sp_rename 'dbo.Orders.OrderCode', 'OrderNumber', 'COLUMN';
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.Null(table.FindColumn("OrderCode"));
        Assert.Equal(SqlTypeCategory.VarChar, table.FindColumn("OrderNumber")!.Type!.Category);
    }

    [Fact]
    public void Build_SpRename_IndexForm_RenamesIndexInPlace()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Old ON dbo.Orders(OrderCode);
            EXEC sp_rename 'dbo.Orders.IX_Old', 'IX_New', 'INDEX';
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.True(table.IsIndexedColumn("OrderCode"));
        Assert.Contains(table.Indexes, i => i.Name == "IX_New");
        Assert.DoesNotContain(table.Indexes, i => i.Name == "IX_Old");
    }

    [Fact]
    public void Build_SpRename_NonLiteralArgument_LedgersAndKeepsOriginalDefinition()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            DECLARE @old sysname = 'dbo.Orders';
            EXEC sp_rename @old, 'PurchaseOrders';
            """);

        Assert.NotNull(catalog.Find("dbo.Orders"));
        Assert.Null(catalog.Find("dbo.PurchaseOrders"));
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename");
    }

    [Fact]
    public void Build_UseStatement_IsLedgeredNotSilentlySwallowed()
    {
        // Genuine cross-database switching is not modeled (no real corpus repo exercises it -
        // see KnownGapCharacterizationTests.CrossDatabaseReference_GetsAKeyNothingPopulates),
        // but USE itself should never be a construct with zero trace.
        var catalog = BuildFrom("""
            USE OtherDb;
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "USE" && e.Reason.Contains("OtherDb", StringComparison.Ordinal));

        // Objects still register against the single implicit target database, unaffected by
        // whatever database name USE happens to name - unchanged from today's behavior.
        Assert.NotNull(catalog.Find("dbo.Orders"));
    }

    [Fact]
    public void Build_ProcedureParameters_RegisteredInDeclarationOrderWithOutputFlag()
    {
        var catalog = BuildFrom("""
            CREATE PROCEDURE dbo.FindOrder
                @OrderId int,
                @Status varchar(20) OUTPUT
            AS
            BEGIN
                RETURN;
            END
            """);

        Assert.True(catalog.TryGetProcedureParameters("dbo.FindOrder", out var parameters));
        Assert.Equal(2, parameters.Count);

        Assert.Equal("@OrderId", parameters[0].Name);
        Assert.Equal(SqlTypeCategory.Int, parameters[0].Type!.Category);
        Assert.False(parameters[0].IsOutput);

        Assert.Equal("@Status", parameters[1].Name);
        Assert.Equal(SqlTypeCategory.VarChar, parameters[1].Type!.Category);
        Assert.True(parameters[1].IsOutput);
    }

    [Fact]
    public void Build_ProcedureNeverDeclared_TryGetProcedureParametersReturnsFalse()
    {
        var catalog = BuildFrom("CREATE TABLE dbo.T (Id int NOT NULL);");

        Assert.False(catalog.TryGetProcedureParameters("dbo.NeverDeclared", out var parameters));
        Assert.Null(parameters);
    }

    [Fact]
    public void Build_ProcedureWithTableValuedParameter_RegistersItWithNullTypeAtItsRealPosition()
    {
        // A TVP still occupies a real positional slot in the parameter list (the procedure call
        // graph's positional-argument matching depends on this) even though it has no SqlType of
        // its own to report - recorded as null, not omitted.
        var catalog = BuildFrom("""
            CREATE TYPE dbo.CodeList AS TABLE (Code varchar(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.Callee (@Codes dbo.CodeList READONLY, @After int) AS SELECT 1;
            """);

        Assert.True(catalog.TryGetProcedureParameters("dbo.Callee", out var parameters));
        Assert.Equal(2, parameters.Count);
        Assert.Equal("@Codes", parameters[0].Name);
        Assert.Null(parameters[0].Type);
        Assert.Equal("@After", parameters[1].Name);
        Assert.Equal(SqlTypeCategory.Int, parameters[1].Type!.Category);
    }

    [Fact]
    public void Build_AlterTableAddFromInsideProcOnBatchLevelTempTable_UpdatesTheOneTrueUnscopedEntry()
    {
        // A #temp table created at BATCH level (outside any procedure) is visible to a
        // subsequently-called procedure in the same session - a real, common pattern (a script
        // builds #t, then calls a proc that further alters/populates it). ALTER TABLE ADD from
        // inside that proc's body finds #t via Find's own scoped-then-unscoped-fallback (there is
        // no "dbo.usp_Test"-scoped entry, only the bare batch-level one), but the write-back scope
        // was computed from the ALTER statement's OWN current scope ("dbo.usp_Test") rather than
        // the scope the entry was actually found under (none) - creating a stray
        // "dbo.usp_Test"::#t duplicate carrying the ALTER's new column, while the real, bare #t
        // entry every OTHER scope (including plain batch-level code, or another unrelated
        // procedure's own fallback lookup) resolves through stayed stale, missing Col2 entirely.
        var catalog = CatalogBuilder.Build(
            [Parse("""
                CREATE TABLE #t (Col1 INT NOT NULL);
                GO
                CREATE PROCEDURE dbo.usp_Test
                AS
                BEGIN
                    ALTER TABLE #t ADD Col2 VARCHAR(20) NOT NULL;
                END
                GO
                CREATE PROCEDURE dbo.usp_Unrelated
                AS
                BEGIN
                    SELECT Col1 FROM #t;
                END
                """)]);

        var batchLevel = catalog.Find("#t");
        // dbo.usp_Unrelated never declares its own #t, so this resolves through the SAME
        // scoped-then-unscoped-fallback path as the ALTER statement itself did - if the ALTER's
        // write-back had gone to a stray "dbo.usp_Test"::#t key instead of the one true unscoped
        // entry, this would see the pre-ALTER, Col2-less shape instead.
        var seenFromUnrelatedScope = catalog.Find("#t", "dbo.usp_Unrelated");

        Assert.NotNull(batchLevel);
        Assert.NotNull(batchLevel!.FindColumn("Col2"));
        Assert.NotNull(seenFromUnrelatedScope);
        Assert.NotNull(seenFromUnrelatedScope!.FindColumn("Col2"));
        Assert.Same(batchLevel, seenFromUnrelatedScope);
    }

    private static SqlParseResult Parse(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result;
    }
}
