using SilentScan.Core.Predicates;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Database-level configuration
/// flags" - <see cref="DatabaseConfigurationReader"/> reads directly off a real database's own
/// <c>sys.databases</c>/<c>sys.database_query_store_options</c> rows.
///
/// <b>Oracle discovery, load-bearing for how the baseline test below is written:</b> a bare
/// <c>CREATE DATABASE</c> on this engine instance genuinely starts with Query Store ON and
/// immediately READ_WRITE (confirmed directly, no warm-up lag) - but every disposable database
/// THIS test suite's own <c>DatabaseProvisioner</c> creates deliberately turns Query Store back
/// OFF right after creation (see its own doc comment: real, measured Docker error-log spam from
/// Query Store's background worker racing this suite's CREATE/DROP churn). So a fresh test
/// database's real, honest starting state already has one flag "unhealthy" by this test
/// infrastructure's own deliberate choice, not a defect in the reader - the baseline test below
/// explicitly re-enables Query Store first to get a genuinely all-defaults database, rather than
/// asserting a premise this suite's own provisioner had already falsified.
/// </summary>
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

        var kinds = findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(DatabaseConfigurationFindingKind.PageVerifyNotChecksum, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.AutoShrinkOn, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, kinds);

        // Query Store is OFF here, so its capture-mode kind must never also fire - it is only
        // evaluated when Query Store's own actual state IS READ_WRITE (see
        // DatabaseConfigurationFinding's own doc comment for why).
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

        var kinds = findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, kinds);
        Assert.DoesNotContain(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, kinds);
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

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Database-option gaps in
/// the shipped DatabaseConfigurationFindingKind stream" - the two new AUTO_CREATE/UPDATE_STATISTICS
/// kinds, each turned OFF explicitly (both default ON on this engine).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DatabaseConfigurationReaderStatisticsFlagsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DatabaseConfigurationReaderStatisticsFlagsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY);
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

        var kinds = findings.Select(f => f.Kind).ToHashSet();
        Assert.Contains(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff, kinds);
        Assert.Contains(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff, kinds);
    }
}

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A - the compatibility-level
/// kind. <c>SilentScan.Verify.Deployment.DatabaseProvisioner</c> pins every disposable oracle
/// database's own compatibility level to 160 right after creation (the same level this engine
/// instance's own <c>model</c> database carries - verified directly), so the ONLY way to get
/// behind the engine's own current default here is to explicitly ALTER it back down after
/// provisioning, which this test's own DDL does.
/// </summary>
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
