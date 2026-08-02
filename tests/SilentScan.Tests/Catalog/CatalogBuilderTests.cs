using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

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
        // A function call inside a computed column expression is a deliberately unresolved
        // case (out of ComputedColumnTypeResolver's narrow scope) - it must stay Unknown AND
        // reach the skip ledger, never silently vanish with no trace.
        var catalog = BuildFrom("""
            CREATE TABLE dbo.T
            (
                Created DATETIME NOT NULL,
                CreatedYear AS (YEAR(Created))
            );
            """);

        var createdYear = catalog.Find("dbo.T")!.FindColumn("CreatedYear")!;

        Assert.Null(createdYear.Type);
        Assert.Contains(catalog.Skipped.Entries, e => e.ConstructKind == "computed column type" && e.Reason.Contains("CreatedYear", StringComparison.Ordinal));
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

    private static SqlParseResult Parse(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result;
    }
}
