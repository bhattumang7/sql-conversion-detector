using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderDefaultsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderDefaultsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET QUERY_STORE = ON;
        GO
        """;

    [Fact]
    public async Task AllDefaults_NeverFires()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Empty(findings);
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderUnhealthyFlagsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderUnhealthyFlagsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET PAGE_VERIFY NONE;
        GO
        ALTER DATABASE CURRENT SET AUTO_SHRINK ON WITH NO_WAIT;
        GO
        ALTER DATABASE CURRENT SET TARGET_RECOVERY_TIME = 0 SECONDS;
        GO
        ALTER DATABASE CURRENT SET QUERY_STORE = OFF;
        GO
        """;

    [Fact]
    public async Task MutatedFlags_EachFiresItsOwnKind()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Equal(4, findings.Count);

        var kinds = findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(DatabaseConfigurationFindingKind.PageVerifyNotChecksum, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.AutoShrinkOn, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, kinds);

        Assert.DoesNotContain(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, kinds);

        Assert.All(findings, f => Assert.Equal(DatabaseName, f.DatabaseName));
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderQueryStoreCaptureModeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderQueryStoreCaptureModeOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET QUERY_STORE = ON (QUERY_CAPTURE_MODE = ALL);
        GO
        """;

    [Fact]
    public async Task QueryStoreOnButCaptureModeNotAuto_FiresOnlyTheCaptureModeKind()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, finding.Kind);
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderAutoCloseOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderAutoCloseOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET AUTO_CLOSE ON;
        GO
        """;

    [Fact]
    public async Task AutoCloseOn_Fires()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Contains(DatabaseConfigurationFindingKind.AutoCloseOn, findings.Select(f => f.Kind));
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderStatisticsFlagsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderStatisticsFlagsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET QUERY_STORE = ON;
        GO
        ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;
        GO
        ALTER DATABASE CURRENT SET AUTO_UPDATE_STATISTICS OFF;
        GO
        """;

    [Fact]
    public async Task BothStatisticsFlagsOff_EachFiresItsOwnKind()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Equal(2, findings.Count);

        var kinds = findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff, kinds);
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderCompatibilityLevelOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderCompatibilityLevelOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;
        GO
        """;

    [Fact]
    public async Task CompatibilityLevelBehindModel_Fires()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.Contains(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault, findings.Select(f => f.Kind));
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderSpatialPersistedComputedColumnOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderSpatialPersistedComputedColumnOracleTests);

    protected override string Ddl => """
        SET ANSI_NULLS ON;
        SET ANSI_PADDING ON;
        SET ANSI_WARNINGS ON;
        SET ARITHABORT ON;
        SET CONCAT_NULL_YIELDS_NULL ON;
        SET QUOTED_IDENTIFIER ON;
        SET NUMERIC_ROUNDABORT OFF;
        GO
        CREATE TABLE dbo.Areas
        (
            Id INT NOT NULL CONSTRAINT PK_Areas PRIMARY KEY,
            Location geography NOT NULL,
            ComparisonLocation geography NOT NULL,
            Distance AS (Location.STDistance(ComparisonLocation)) PERSISTED,
            Buffered AS (Location.STBuffer(1)) PERSISTED
        );
        GO
        INSERT dbo.Areas (Id, Location, ComparisonLocation)
        VALUES (1, geography::Point(0, 0, 4326), geography::Point(1, 1, 4326));
        GO
        CREATE INDEX IX_Areas_Distance ON dbo.Areas(Distance);
        GO
        CREATE SPATIAL INDEX SIX_Areas_Location ON dbo.Areas(Location) USING GEOGRAPHY_GRID;
        GO
        """;

    public override async Task InitializeAsync()
    {
        await using (var connection = new SqlConnection(Options.BuildConnectionString(database: null)))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{DatabaseName}];";
            await command.ExecuteNonQueryAsync();
            command.CommandText = $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 100;";
            await command.ExecuteNonQueryAsync();
        }

        await new ScriptDeployer(Options).DeployAsync(Ddl, DatabaseName);
    }

    [Fact]
    public async Task SpatialPersistedComputedColumnDisabledByCompatibilityChange_Fires()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var finding = Assert.Single(findings, f => f.Kind == DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange);
        Assert.NotNull(finding.AffectedObjectName);
        Assert.Equal("geography::STBuffer", finding.Dependency);
        Assert.Equal(160, finding.TargetCompatibilityLevel);
    }
}

[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderPlanGuideOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderPlanGuideOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
        GO
        ALTER DATABASE CURRENT SET QUERY_STORE = ON;
        GO
        EXEC sp_create_plan_guide
            @name = N'PG_Enabled',
            @stmt = N'SELECT Id FROM dbo.T WHERE Id = 1',
            @type = N'SQL',
            @module_or_batch = NULL,
            @params = NULL,
            @hints = N'OPTION (RECOMPILE)';
        GO
        EXEC sp_create_plan_guide
            @name = N'PG_Disabled',
            @stmt = N'SELECT Id FROM dbo.T WHERE Id = 2',
            @type = N'SQL',
            @module_or_batch = NULL,
            @params = NULL,
            @hints = N'OPTION (RECOMPILE)';
        GO
        EXEC sp_control_plan_guide @operation = N'DISABLE', @name = N'PG_Disabled';
        GO
        """;

    [Fact]
    public async Task OnlyEnabledPlanGuide_FiresWithNameScopeAndHints()
    {
        var findings = await new DatabaseConfigurationReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var finding = Assert.Single(findings, f => f.Kind == DatabaseConfigurationFindingKind.PlanGuideAltersOptimization);
        Assert.Equal("PG_Enabled", finding.AffectedObjectName);
        Assert.Equal("SQL", finding.PlanGuideScopeType);
        Assert.Equal("OPTION (RECOMPILE)", finding.PlanGuideHints);
    }
}
