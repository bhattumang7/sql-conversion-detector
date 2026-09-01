using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class GeneratedAlwaysColumnAssignmentOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(GeneratedAlwaysColumnAssignmentOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Widget
        (
            Id   INT NOT NULL PRIMARY KEY,
            Code VARCHAR(20) NOT NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
        )
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.WidgetHistory));
        GO
        CREATE TABLE dbo.PlainWidget
        (
            Id INT NOT NULL PRIMARY KEY,
            ValidFrom DATETIME2 NOT NULL,
            ValidTo   DATETIME2 NOT NULL
        );
        GO
        """;

    private async Task<IReadOnlyList<GeneratedAlwaysColumnAssignmentFinding>> ScanAsync(string sql)
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var parseResult = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        return GeneratedAlwaysColumnAssignmentScanner.Scan(parseResult, catalog);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ExplicitInsertValue_RaisesMsg13536()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("INSERT INTO dbo.Widget (Id, Code, ValidFrom) VALUES (1, 'A', SYSUTCDATETIME());"));

        Assert.Equal(13536, exception.Number);
    }

    [Fact]
    public async Task UpdateSetPeriodColumn_RaisesMsg13537()
    {
        await ExecuteAsync("INSERT INTO dbo.Widget (Id, Code) VALUES (1, 'A');");

        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("UPDATE dbo.Widget SET ValidFrom = SYSUTCDATETIME() WHERE Id = 1;"));

        Assert.Equal(13537, exception.Number);
    }

    [Fact]
    public async Task ExplicitInsertValueIntoRowStart_Fires()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget (Id, Code, ValidFrom) VALUES (1, 'A', SYSUTCDATETIME());");

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, finding.Kind);
        Assert.Equal("ValidFrom", finding.ColumnName, ignoreCase: true);
    }

    [Fact]
    public async Task ExplicitInsertValueIntoRowEnd_Fires()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget (Id, Code, ValidTo) VALUES (2, 'B', '9999-12-31 23:59:59.9999999');");

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, finding.Kind);
        Assert.Equal("ValidTo", finding.ColumnName, ignoreCase: true);
    }

    [Fact]
    public async Task InsertDefaultIntoPeriodColumn_DoesNotFire()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget (Id, Code, ValidFrom) VALUES (3, 'C', DEFAULT);");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task InsertExcludingPeriodColumns_DoesNotFire()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget (Id, Code) VALUES (4, 'D');");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ImplicitColumnListExplicitValue_Fires()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget VALUES (5, 'E', SYSUTCDATETIME(), SYSUTCDATETIME());");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, f.Kind));
        Assert.Contains(findings, f => string.Equals(f.ColumnName, "ValidFrom", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(findings, f => string.Equals(f.ColumnName, "ValidTo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImplicitColumnListDefault_DoesNotFire()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget VALUES (6, 'F', DEFAULT, DEFAULT);");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task InsertSelectNamingPeriodColumn_Fires()
    {
        var findings = await ScanAsync("INSERT INTO dbo.Widget (Id, Code, ValidFrom) SELECT 7, 'G', SYSUTCDATETIME();");

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, finding.Kind);
    }

    [Fact]
    public async Task UpdateSetPeriodColumn_Fires()
    {
        var findings = await ScanAsync("UPDATE dbo.Widget SET ValidFrom = SYSUTCDATETIME() WHERE Id = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue, finding.Kind);
        Assert.Equal("ValidFrom", finding.ColumnName, ignoreCase: true);
    }

    [Fact]
    public async Task UpdateSetPeriodColumnToDefault_StillFires()
    {
        var findings = await ScanAsync("UPDATE dbo.Widget SET ValidFrom = DEFAULT WHERE Id = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue, finding.Kind);
    }

    [Fact]
    public async Task UpdateNonPeriodColumn_DoesNotFire()
    {
        var findings = await ScanAsync("UPDATE dbo.Widget SET Code = 'Z' WHERE Id = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MergeInsertExplicitValue_Fires()
    {
        var findings = await ScanAsync("""
            MERGE dbo.Widget AS tgt
            USING (SELECT 8 AS Id, 'H' AS Code) AS src ON tgt.Id = src.Id
            WHEN NOT MATCHED THEN INSERT (Id, Code, ValidFrom) VALUES (src.Id, src.Code, SYSUTCDATETIME());
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue, finding.Kind);
    }

    [Fact]
    public async Task MergeInsertDefault_DoesNotFire()
    {
        var findings = await ScanAsync("""
            MERGE dbo.Widget AS tgt
            USING (SELECT 9 AS Id, 'I' AS Code) AS src ON tgt.Id = src.Id
            WHEN NOT MATCHED THEN INSERT (Id, Code, ValidFrom) VALUES (src.Id, src.Code, DEFAULT);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MergeUpdateSetPeriodColumn_Fires()
    {
        var findings = await ScanAsync("""
            MERGE dbo.Widget AS tgt
            USING (SELECT 1 AS Id, 'J' AS Code) AS src ON tgt.Id = src.Id
            WHEN MATCHED THEN UPDATE SET Code = src.Code, ValidFrom = SYSUTCDATETIME();
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue, finding.Kind);
    }

    [Fact]
    public async Task NonTemporalTableWithSameColumnNames_NeverFires()
    {
        var findings = await ScanAsync("INSERT INTO dbo.PlainWidget (Id, ValidFrom, ValidTo) VALUES (1, SYSUTCDATETIME(), SYSUTCDATETIME());");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task RealCatalog_GeneratedAlwaysColumnFlag_MatchesLiveSysColumns()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();

        var table = Assert.Single(catalog.Tables, t => string.Equals(t.QualifiedName, "dbo.Widget", StringComparison.OrdinalIgnoreCase));
        var validFrom = Assert.Single(table.Columns, c => string.Equals(c.Name, "ValidFrom", StringComparison.OrdinalIgnoreCase));
        var validTo = Assert.Single(table.Columns, c => string.Equals(c.Name, "ValidTo", StringComparison.OrdinalIgnoreCase));
        var code = Assert.Single(table.Columns, c => string.Equals(c.Name, "Code", StringComparison.OrdinalIgnoreCase));
        Assert.True(validFrom.IsGeneratedAlwaysPeriod);
        Assert.True(validTo.IsGeneratedAlwaysPeriod);
        Assert.False(code.IsGeneratedAlwaysPeriod);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generated_always_type FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Widget') AND name = 'ValidFrom';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetByte(0));
    }
}
