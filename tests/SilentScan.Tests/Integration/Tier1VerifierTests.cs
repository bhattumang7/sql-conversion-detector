using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class Tier1VerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanTier1VerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly Tier1Verifier _verifier;
    private DatabaseCatalog _catalog = null!;

    public Tier1VerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new Tier1Verifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);

        const string ddl = """
            CREATE TABLE dbo.T1Indexed (Code VARCHAR(20) NOT NULL);
            GO
            CREATE INDEX IX_T1Indexed_Code ON dbo.T1Indexed(Code);
            GO
            CREATE TABLE dbo.T1Unindexed (Code VARCHAR(20) NOT NULL, UnindexedLob VARCHAR(MAX) NOT NULL);
            GO
            """;
        await new ScriptDeployer(_options).DeployAsync(ddl, DatabaseName);

        var parseResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        _catalog = CatalogBuilder.Build([parseResult]);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static SargabilityFinding Finding(
        SargabilityFindingKind kind, string tableQualifiedName, string columnName, bool? indexed, string predicateFragmentText) =>
        new(kind, columnName, Detail: null, "file.sql", 1, 1, TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: predicateFragmentText);

    [Fact]
    public async Task VerifyAsync_FunctionWrappedColumnOnIndexedColumn_ConfirmsNoIndexSeek()
    {
        var finding = Finding(SargabilityFindingKind.FunctionWrappedColumn, "dbo.T1Indexed", "Code", indexed: true, "UPPER(Code)");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_BareColumnOnIndexedColumn_IsNotConfirmed()
    {
        var finding = Finding(SargabilityFindingKind.FunctionWrappedColumn, "dbo.T1Indexed", "Code", indexed: true, "Code");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LeadingWildcardLikeOnIndexedColumn_ConfirmsNoIndexSeek()
    {
        var finding = Finding(SargabilityFindingKind.LeadingWildcardLike, "dbo.T1Indexed", "Code", indexed: true, "Code LIKE '%abc'");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnHasNoDeployedIndex_ConfirmsViaScratchIndex()
    {
        var finding = Finding(SargabilityFindingKind.FunctionWrappedColumn, "dbo.T1Unindexed", "Code", indexed: false, "UPPER(Code)");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.ConfirmedViaScratchIndex, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnTypeCannotBeIndexed_FallsBackToConfirmedUnindexed()
    {
        var finding = Finding(SargabilityFindingKind.FunctionWrappedColumn, "dbo.T1Unindexed", "UnindexedLob", indexed: false, "UPPER(UnindexedLob)");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.UnindexedNotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NoPredicateFragmentText_ReturnsNotProbeable()
    {
        var finding = Finding(SargabilityFindingKind.FunctionWrappedColumn, "dbo.T1Indexed", "Code", indexed: true, predicateFragmentText: null!);

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_TableNoLongerInDeployedSchema_ReturnsProbeFailed()
    {
        var finding = Finding(SargabilityFindingKind.LeadingWildcardLike, "dbo.DoesNotExist", "Code", indexed: true, "Code LIKE '%abc'");

        var result = await _verifier.VerifyAsync(DatabaseName, finding, _catalog);

        Assert.Equal(Tier1Outcome.ProbeFailed, result.Outcome);
    }
}
