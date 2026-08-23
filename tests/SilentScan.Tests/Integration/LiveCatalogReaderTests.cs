using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveCatalogReaderTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveCatalogReaderTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (
            CustomerId INT NOT NULL PRIMARY KEY,
            Email varchar(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            DisplayName AS (Email + '!'),
            INDEX IX_Email (Email));
        GO
        CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NOT NULL;
        GO
        CREATE TABLE dbo.OrdersFk (
            OrderId INT NOT NULL PRIMARY KEY,
            CustomerCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        GO
        CREATE TABLE dbo.CustomersFk (
            CustomerCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY);
        GO
        ALTER TABLE dbo.OrdersFk ADD CONSTRAINT FK_OrdersFk_CustomersFk
            FOREIGN KEY (CustomerCode) REFERENCES dbo.CustomersFk (CustomerCode);
        GO
        CREATE TABLE dbo.Parents (Id INT NOT NULL PRIMARY KEY);
        GO
        CREATE TABLE dbo.CascadeChildren (Id INT NOT NULL PRIMARY KEY, ParentId INT NULL);
        GO
        ALTER TABLE dbo.CascadeChildren ADD CONSTRAINT FK_CascadeChildren_Parents
            FOREIGN KEY (ParentId) REFERENCES dbo.Parents (Id) ON DELETE CASCADE ON UPDATE SET NULL;
        GO
        CREATE TABLE dbo.UntrustedChildren (Id INT NOT NULL PRIMARY KEY, ParentId INT NULL);
        GO
        ALTER TABLE dbo.UntrustedChildren WITH NOCHECK ADD CONSTRAINT FK_UntrustedChildren_Parents
            FOREIGN KEY (ParentId) REFERENCES dbo.Parents (Id);
        GO
        CREATE TABLE dbo.CheckedOrders (Id INT NOT NULL PRIMARY KEY, Amount INT NOT NULL);
        GO
        ALTER TABLE dbo.CheckedOrders WITH NOCHECK ADD CONSTRAINT CK_CheckedOrders_Amount CHECK (Amount > 0);
        GO
        CREATE TABLE dbo.Widget (
            WidgetId INT NOT NULL PRIMARY KEY,
            Code VARCHAR(50) NOT NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.WidgetHistory));
        GO
        CREATE NONCLUSTERED INDEX IX_Widget_Code ON dbo.Widget(Code);
        GO
        CREATE TABLE dbo.Gadget (
            GadgetId INT NOT NULL PRIMARY KEY,
            Code VARCHAR(50) NOT NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.GadgetHistory));
        GO
        CREATE NONCLUSTERED INDEX IX_Gadget_Code ON dbo.Gadget(Code);
        GO
        CREATE NONCLUSTERED INDEX IX_GadgetHistory_Code ON dbo.GadgetHistory(Code);
        GO
        CREATE TABLE dbo.OrderedTriggers (Id INT NOT NULL PRIMARY KEY);
        GO
        CREATE TRIGGER dbo.trg_OrderedTriggers_1 ON dbo.OrderedTriggers AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
        GO
        CREATE TRIGGER dbo.trg_OrderedTriggers_2 ON dbo.OrderedTriggers AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
        GO
        EXEC sp_settriggerorder @triggername = N'dbo.trg_OrderedTriggers_1', @order = N'First', @stmttype = N'INSERT';
        """;

    [Fact]
    public async Task ReadAsync_TableColumns_MatchDeployedDdl()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        Assert.Equal("dbo", table.SchemaName);

        var customerId = table.FindColumn("CustomerId");
        Assert.NotNull(customerId);
        Assert.Equal(SqlTypeCategory.Int, customerId!.Type!.Category);
        Assert.False(customerId.IsComputed);

        var email = table.FindColumn("Email");
        Assert.NotNull(email);
        Assert.Equal(SqlTypeCategory.VarChar, email!.Type!.Category);
        Assert.Equal(100, email.Type.Length);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", email.Type.Collation?.Name);
        Assert.False(email.IsNullable);
    }

    [Fact]
    public async Task ReadAsync_ComputedColumn_TypeIsEngineResolvedNotReDerived()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        var displayName = table.FindColumn("DisplayName");
        Assert.NotNull(displayName);
        Assert.True(displayName!.IsComputed);
        Assert.False(displayName.IsPersisted);
        Assert.NotNull(displayName.Type);
        Assert.Equal(SqlTypeCategory.VarChar, displayName.Type!.Category);
    }

    [Fact]
    public async Task ReadAsync_IndexedColumn_IsFlaggedIndexed()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => t.Name == "Customers");
        Assert.True(table.IsIndexedColumn("Email"));
        Assert.True(table.IsIndexedColumn("CustomerId"));
        Assert.False(table.IsIndexedColumn("DisplayName"));
    }

    [Fact]
    public async Task ReadAsync_TypeAlias_ResolvesToUnderlyingType()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.TypeAliases.TryGetValue("dbo.PhoneNumber", out var underlying));
        Assert.Equal(SqlTypeCategory.VarChar, underlying!.Category);
        Assert.Equal(20, underlying.Length);
    }

    [Fact]
    public async Task ReadAsync_DatabaseDefaultCollation_MatchesDeployedDatabase()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(128));";
        var realCollation = (string)(await command.ExecuteScalarAsync())!;

        Assert.NotNull(catalog.DefaultCollation);
        Assert.Equal(realCollation, catalog.DefaultCollation!.Name);
    }

    [Fact]
    public async Task ReadAsync_ForeignKey_ReadsRealConstraintFromSysForeignKeyColumns()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var fk = Assert.Single(catalog.ForeignKeys, f => f.ConstraintName == "FK_OrdersFk_CustomersFk");
        Assert.Equal("dbo.OrdersFk", fk.ParentTableQualifiedName);
        Assert.Equal("CustomerCode", fk.ParentColumnName);
        Assert.Equal("dbo.CustomersFk", fk.ReferencedTableQualifiedName);
        Assert.Equal("CustomerCode", fk.ReferencedColumnName);
    }

    [Fact]
    public async Task ReadAsync_MatchingForeignKey_CrossTableTypeDriftScannerNeverFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ReadAsync_CascadingForeignKey_ReadsRealActionsFromSysForeignKeys()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var fk = Assert.Single(catalog.ForeignKeys, f => f.ConstraintName == "FK_CascadeChildren_Parents");
        Assert.Equal(ReferentialAction.Cascade, fk.DeleteAction);
        Assert.Equal(ReferentialAction.SetNull, fk.UpdateAction);
        Assert.False(fk.IsNotTrusted);
    }

    [Fact]
    public async Task ReadAsync_OrdinaryForeignKey_NoActionAndTrusted()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var fk = Assert.Single(catalog.ForeignKeys, f => f.ConstraintName == "FK_OrdersFk_CustomersFk");
        Assert.Equal(ReferentialAction.NoAction, fk.DeleteAction);
        Assert.Equal(ReferentialAction.NoAction, fk.UpdateAction);
        Assert.False(fk.IsNotTrusted);
    }

    [Fact]
    public async Task ReadAsync_UntrustedForeignKey_ReadsRealIsNotTrustedFromSysForeignKeys()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var fk = Assert.Single(catalog.ForeignKeys, f => f.ConstraintName == "FK_UntrustedChildren_Parents");
        Assert.True(fk.IsNotTrusted);
    }

    [Fact]
    public async Task ReadAsync_UntrustedCheckConstraint_ReadsRealIsNotTrustedFromSysCheckConstraints()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var check = Assert.Single(catalog.CheckConstraints, c => c.ConstraintName == "CK_CheckedOrders_Amount");
        Assert.Equal("dbo.CheckedOrders", check.TableQualifiedName);
        Assert.True(check.IsNotTrusted);
        Assert.False(check.IsDisabled);
    }

    [Fact]
    public async Task ReadAsync_TriggerFiringOrder_ReadsRealPinStateFromSysTriggerEvents()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var pinned = Assert.Single(catalog.TriggerEvents, e => e.TriggerQualifiedName == "dbo.trg_OrderedTriggers_1");
        Assert.Equal("dbo.OrderedTriggers", pinned.TableQualifiedName);
        Assert.Equal("INSERT", pinned.EventTypeDescription);
        Assert.True(pinned.IsFirst);
        Assert.False(pinned.IsLast);
        Assert.False(pinned.IsInsteadOf);
        Assert.False(pinned.IsDisabled);

        var unpinned = Assert.Single(catalog.TriggerEvents, e => e.TriggerQualifiedName == "dbo.trg_OrderedTriggers_2");
        Assert.False(unpinned.IsFirst);
        Assert.False(unpinned.IsLast);

        var findings = TriggerOrderScanner.Scan(catalog);
        Assert.DoesNotContain(findings, f => f.TableQualifiedName == "dbo.OrderedTriggers");
    }

    [Fact]
    public async Task ReadAsync_UntrustedConstraints_UntrustedConstraintScannerFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = UntrustedConstraintScanner.Scan(catalog);

        Assert.Contains(findings, f => f.Kind == UntrustedConstraintFindingKind.ForeignKey && f.ConstraintName == "FK_UntrustedChildren_Parents");
        Assert.Contains(findings, f => f.Kind == UntrustedConstraintFindingKind.CheckConstraint && f.ConstraintName == "CK_CheckedOrders_Amount");
        Assert.DoesNotContain(findings, f => f.ConstraintName == "FK_OrdersFk_CustomersFk");
    }

    [Fact]
    public async Task ReadAsync_CascadingForeignKey_CascadingForeignKeyScannerFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = CascadingForeignKeyScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.ConstraintName == "FK_CascadeChildren_Parents");
        Assert.Equal(ReferentialAction.Cascade, finding.DeleteAction);
        Assert.Equal(ReferentialAction.SetNull, finding.UpdateAction);
    }

    [Fact]
    public async Task ReadAsync_TemporalTable_ReadsRealPairingFromSysTables()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var pair = Assert.Single(catalog.TemporalTablePairs, p => p.CurrentTableQualifiedName == "dbo.Widget");
        Assert.Equal("dbo.WidgetHistory", pair.HistoryTableQualifiedName);
    }

    [Fact]
    public async Task ReadAsync_TemporalTable_BothSidesReadAsOrdinaryTablesWithRealIndexes()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var current = catalog.Find("dbo.Widget");
        Assert.NotNull(current);
        Assert.True(current!.IsIndexedColumn("Code"));

        var history = catalog.Find("dbo.WidgetHistory");
        Assert.NotNull(history);
        Assert.DoesNotContain(history!.Indexes, i => i.KeyColumns.Contains("Code", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadAsync_TemporalTableMissingHistoryIndex_TemporalTableHistoryIndexGapScannerFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = TemporalTableHistoryIndexGapScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.CurrentTableQualifiedName == "dbo.Widget");
        Assert.Equal("dbo.WidgetHistory", finding.HistoryTableQualifiedName);
        Assert.Equal("IX_Widget_Code", finding.CurrentIndexName);
        Assert.Equal(["Code"], finding.KeyColumns);
    }

    [Fact]
    public async Task ReadAsync_TemporalTableWithMatchingHistoryIndex_TemporalTableHistoryIndexGapScannerNeverFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = TemporalTableHistoryIndexGapScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.CurrentTableQualifiedName == "dbo.Gadget");
    }

    [Fact]
    public async Task ReadAsync_TemporalTable_PrimaryKeyNeverFlaggedAgainstHistorySide()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = TemporalTableHistoryIndexGapScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.KeyColumns.Contains("WidgetId"));
    }
}
