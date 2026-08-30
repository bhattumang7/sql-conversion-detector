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
    public void Build_ColumnWithNoExplicitNullability_UnderAnsiNullDfltOffOn_DefaultsToNotNull()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("SET ANSI_NULL_DFLT_OFF ON; CREATE TABLE dbo.T (Col INT);")]);

        Assert.False(catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable);
    }

    [Fact]
    public void Build_ColumnWithNoExplicitNullability_UnderAnsiNullDfltOnOn_DefaultsToNullable()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("SET ANSI_NULL_DFLT_ON ON; CREATE TABLE dbo.T (Col INT);")]);

        Assert.True(catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable);
    }

    [Fact]
    public void Build_ColumnWithNoExplicitNullability_UnderDatabaseAnsiNullDefaultOff_DefaultsToNotNull()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE TABLE dbo.T (Col INT);")], manifestAnsiNullDefaultOn: false);

        Assert.False(catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable);
    }

    [Fact]
    public void Build_ColumnWithNoExplicitNullability_InScriptSetOverridesDatabaseAnsiNullDefault()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("SET ANSI_NULL_DFLT_ON ON; CREATE TABLE dbo.T (Col INT);")], manifestAnsiNullDefaultOn: false);

        Assert.True(catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable);
    }

    [Fact]
    public void Build_ComputedColumnWithNoExplicitNullability_IgnoresAnsiNullDfltAndDefaultsToNullable()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("SET ANSI_NULL_DFLT_OFF ON; CREATE TABLE dbo.T (A INT NOT NULL, Col AS (A + 1));")]);

        Assert.True(catalog.Find("dbo.T")!.FindColumn("Col")!.IsNullable);
    }

    [Fact]
    public void Build_SpatialColumn_TypeIsNullAndLedgered()
    {

        var catalog = BuildFrom("CREATE TABLE dbo.Cities (Id INT NOT NULL, Location GEOGRAPHY NULL);");

        Assert.Null(catalog.Find("dbo.Cities")!.FindColumn("Location")!.Type);
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "column type" && e.Reason.Contains("Location", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ComputedColumnWithNoDeclaredType_NotLedgered()
    {

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

        var catalog = BuildFrom("ALTER ASSEMBLY MyAssembly FROM 0x4D5A;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CLR assembly" && e.Reason.Contains("MyAssembly", StringComparison.Ordinal) && e.Reason.Contains("ALTER ASSEMBLY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AlterIndexDisable_ColumnNoLongerReportsIndexed()
    {

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

        var catalog = BuildFrom("CREATE TABLE dbo.Likes AS EDGE;");

        Assert.Null(catalog.Find("dbo.Likes"));
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CREATE TABLE" && e.Reason.Contains("Likes", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DropProcedure_DoesNotThrowAndLeavesUnrelatedCatalogDataIntact()
    {

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
    public void Build_SpRename_ObjectForm_UnqualifiedName_ResolvesAgainstDefaultSchema()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            EXEC sp_rename 'Orders', 'PurchaseOrders';
            """);

        Assert.Null(catalog.Find("dbo.Orders"));
        Assert.NotNull(catalog.Find("dbo.PurchaseOrders"));
    }

    [Fact]
    public void Build_SpRename_ObjectForm_ThreePartName_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            EXEC sp_rename 'somedb.dbo.Orders', 'PurchaseOrders';
            """);

        Assert.NotNull(catalog.Find("dbo.Orders"));
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename" && e.Reason.Contains("three-part", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SpRename_ObjectForm_UnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("EXEC sp_rename 'dbo.NeverSeen', 'Whatever';");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SpRename_ColumnForm_UnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("EXEC sp_rename 'dbo.NeverSeen.OrderCode', 'OrderNumber', 'COLUMN';");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename (COLUMN)" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SpRename_IndexForm_UnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("EXEC sp_rename 'dbo.NeverSeen.IX_Old', 'IX_New', 'INDEX';");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename (INDEX)" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SpRename_UnmodeledObjectType_RecordsSkippedEntryAndKeepsOriginalDefinition()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL, CONSTRAINT PK_Orders PRIMARY KEY (OrderCode));
            EXEC sp_rename 'PK_Orders', 'PK_Orders_New', 'CONSTRAINT';
            """);

        Assert.NotNull(catalog.Find("dbo.Orders"));
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "sp_rename" && e.Reason.Contains("'CONSTRAINT'", StringComparison.Ordinal));
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

        var catalog = BuildFrom("""
            USE OtherDb;
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "USE" && e.Reason.Contains("OtherDb", StringComparison.Ordinal));

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

    [Fact]
    public void Build_AlterIndexOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("ALTER INDEX IX_X ON dbo.NeverSeen DISABLE;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "ALTER INDEX" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DropIndexLegacySyntax_TableColumnNoLongerCountsAsIndexed()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            DROP INDEX dbo.Orders.IX_Orders_OrderCode;
            """);

        var table = catalog.Find("dbo.Orders")!;
        Assert.False(table.IsIndexedColumn("OrderCode"));
    }

    [Fact]
    public void Build_DropIndexOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("DROP INDEX IX_X ON dbo.NeverSeen;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "DROP INDEX" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DropIndexNotInCatalog_RecordsSkippedEntryAndLeavesExistingIndexesIntact()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            DROP INDEX IX_NeverCreated ON dbo.Orders;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "DROP INDEX" && e.Reason.Contains("IX_NeverCreated", StringComparison.Ordinal));
        Assert.True(catalog.Find("dbo.Orders")!.IsIndexedColumn("OrderCode"));
    }

    [Fact]
    public void Build_DropSynonym_SynonymNoLongerResolves()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            CREATE SYNONYM dbo.OrdersAlias FOR dbo.Orders;
            DROP SYNONYM dbo.OrdersAlias;
            """);

        Assert.Equal("dbo.OrdersAlias", catalog.ResolveSynonymName("dbo.OrdersAlias"));
    }

    [Fact]
    public void Build_AlterColumnOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("ALTER TABLE dbo.NeverSeen ALTER COLUMN Col INT NOT NULL;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "ALTER TABLE ALTER COLUMN" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AlterColumnOnUnknownColumn_RecordsSkippedEntryAndLeavesTableIntact()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
            ALTER TABLE dbo.Orders ALTER COLUMN NeverExisted INT NOT NULL;
            """);

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "ALTER TABLE ALTER COLUMN" && e.Reason.Contains("NeverExisted", StringComparison.Ordinal));
        Assert.Equal(SqlTypeCategory.VarChar, catalog.Find("dbo.Orders")!.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public void Build_DropColumnOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("ALTER TABLE dbo.NeverSeen DROP COLUMN Col;");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "ALTER TABLE DROP" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateIndexOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("CREATE INDEX IX_X ON dbo.NeverSeen(Col);");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CREATE INDEX" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_CreateColumnstoreIndexOnUnresolvedTable_RecordsSkippedEntry()
    {
        var catalog = BuildFrom("CREATE COLUMNSTORE INDEX IX_X ON dbo.NeverSeen(Col);");

        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "CREATE COLUMNSTORE INDEX" && e.Reason.Contains("dbo.NeverSeen", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MemoryOptimizedTable_FlagIsCaptured()
    {
        var catalog = BuildFrom(
            "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON);");

        Assert.True(catalog.Find("dbo.T")!.IsMemoryOptimized);
    }

    [Fact]
    public void Build_OrdinaryTable_IsNotFlaggedMemoryOptimized()
    {
        var catalog = BuildFrom("CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);");

        Assert.False(catalog.Find("dbo.T")!.IsMemoryOptimized);
    }

    [Fact]
    public void Build_ColumnsWithMixedEncryption_EachResolvesToItsOwnEncryptionType()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.Customers
            (
                Ssn NVARCHAR(20)
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    NOT NULL,
                Notes NVARCHAR(20)
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    NULL,
                Plain NVARCHAR(20) NULL
            );
            """);

        var table = catalog.Find("dbo.Customers")!;

        Assert.Equal(ColumnEncryptionType.Deterministic, table.FindColumn("Ssn")!.EncryptionType);
        Assert.Equal(ColumnEncryptionType.Randomized, table.FindColumn("Notes")!.EncryptionType);
        Assert.Equal(ColumnEncryptionType.None, table.FindColumn("Plain")!.EncryptionType);
    }

    [Fact]
    public void Build_InlineTableValuedFunction_RegistersInlineKind()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_GetActive()
            RETURNS TABLE
            AS
            RETURN (SELECT 1 AS Id);
            """);

        Assert.True(catalog.TryGetTableValuedFunctionKind("dbo.fn_GetActive", out var kind));
        Assert.Equal(TableValuedFunctionKind.Inline, kind);
    }

    [Fact]
    public void Build_MultiStatementTableValuedFunction_RegistersMultiStatementKind()
    {
        var catalog = BuildFrom("""
            CREATE FUNCTION dbo.fn_GetCodes()
            RETURNS @t TABLE (Code VARCHAR(20) NOT NULL)
            AS
            BEGIN
                RETURN;
            END
            """);

        Assert.True(catalog.TryGetTableValuedFunctionKind("dbo.fn_GetCodes", out var kind));
        Assert.Equal(TableValuedFunctionKind.MultiStatement, kind);
    }

    [Fact]
    public void Build_ClrTableValuedFunction_RegistersClrKind()
    {
        var catalog = CatalogBuilder.Build(
            [Parse("CREATE FUNCTION dbo.fn_Clr() RETURNS TABLE (Col INT NOT NULL) AS EXTERNAL NAME MyAssembly.[MyClass].[MyMethod];")]);

        Assert.True(catalog.TryGetTableValuedFunctionKind("dbo.fn_Clr", out var kind));
        Assert.Equal(TableValuedFunctionKind.Clr, kind);
    }

    [Fact]
    public void Build_TableValuedParameterOfUnresolvableType_IsNotRegisteredAsTableValued()
    {
        var catalog = BuildFrom(
            """
            CREATE PROCEDURE dbo.usp_Ordinary (@Codes dbo.NotACatalogedType READONLY, @After INT)
            AS
            BEGIN
                RETURN;
            END
            """);

        Assert.Null(catalog.Find("@Codes", "dbo.usp_Ordinary"));

        Assert.True(catalog.TryGetProcedureParameters("dbo.usp_Ordinary", out var parameters));
        Assert.Equal("@Codes", parameters[0].Name);
        Assert.Null(parameters[0].Type);
    }

    [Fact]
    public void Build_AlterTableDropConstraint_RemovesTheIndexBackingIt()
    {
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T (Id INT NOT NULL, CONSTRAINT UQ_T_Id UNIQUE (Id));
            ALTER TABLE dbo.T DROP CONSTRAINT UQ_T_Id;
            """);

        var table = catalog.Find("dbo.T")!;

        Assert.DoesNotContain(table.Indexes, i => i.Name == "UQ_T_Id");
        Assert.False(table.IsIndexedColumn("Id"));
    }

}
