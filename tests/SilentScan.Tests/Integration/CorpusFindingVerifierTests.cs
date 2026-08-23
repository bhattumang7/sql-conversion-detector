using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CorpusFindingVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanFindingVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly CorpusFindingVerifier _verifier;

    public CorpusFindingVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new CorpusFindingVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.CodeFrequency (Code CHAR(1) NOT NULL PRIMARY KEY);
            GO
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            GO
            CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, CustomerCode VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_Customers_CustomerCode ON dbo.Customers(CustomerCode);
            GO
            CREATE TABLE dbo.Unindexed (UnindexedId INT NOT NULL, UnindexedCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, UnindexedLob VARCHAR(MAX) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;
            GO
            CREATE FUNCTION dbo.SplitStrings_CTE (@List NVARCHAR(MAX), @Delimiter NVARCHAR(255))
            RETURNS @Items TABLE (Item NVARCHAR(4000))
            WITH SCHEMABINDING
            AS
            BEGIN
                INSERT INTO @Items SELECT 'x';
                RETURN;
            END;
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static PredicateOperand.Column ColumnOperand(string table, string column, SqlType type, bool indexed = false) =>
        new(table, column, type, indexed, Depth: 0, new ColumnProvenance.BaseColumn(table, column, type));

    [Fact]
    public async Task VerifyAsync_CharColumnVsIntLiteral_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "<>",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SqlCollationVarcharColumnVsNVarcharValue_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_Depth1FindingThroughView_ProbesTheViewAndConfirmsAgainstTheBaseColumn()
    {
        var baseColumnType = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var column = new PredicateOperand.Column(
            "dbo.Orders", "OrderCode", baseColumnType, Indexed: true, Depth: 1,
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", baseColumnType, Depth: 1),
            ImmediateRelationQualifiedName: "dbo.vw_Orders", ImmediateColumnName: "OrderCode");

        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            column,
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var probe = CorpusFindingProbeBuilder.Build(finding);
        Assert.Contains("FROM [dbo].[vw_Orders]", probe, StringComparison.Ordinal);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_WindowsCollationVarcharColumnVsNVarcharValue_RangeSeekVerdict_IsConfirmed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.RangeSeek,
            ColumnOperand("dbo.Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SqlCollationColumn_RangeSeekVerdictButPlanIsActuallyScanForced_IsNotConfirmed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.RangeSeek,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_WindowsCollationColumn_ScanForcedVerdictButPlanIsActuallyRangeSeek_IsNotConfirmed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LiteralOperand_ProbesTheReconstructedLiteralNotAVariable()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 5), IsLiteral: true, LiteralText: "N'Alice'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LiteralOperandThatCannotBeReconstructed_ReturnsNotProbeableWithFidelityCaveat()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int), IsLiteral: true, LiteralText: null),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotProbeable, result.Outcome);
        Assert.Contains("misrepresent probe fidelity", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_IntColumnVsBigIntValue_SameFamilyWidening_IsNotConfirmed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.BigInt)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnVsColumnAcrossTables_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnprobeableOtherOperandType_ReturnsNotProbeable()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.UserDefined)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ScanForcedColumnHasNoDeployedIndex_ConfirmsViaScratchIndex()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Unindexed", "UnindexedCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20), IsLiteral: true, LiteralText: "N'ABC'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedViaScratchIndex, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ScanForcedColumnTypeCannotBeIndexed_FallsBackToConfirmedUnindexed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Unindexed", "UnindexedLob", new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20), IsLiteral: true, LiteralText: "N'ABC'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedUnindexed, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.Confirmed, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.ConfirmedViaScratchIndex, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ScanForcedColumnNoLongerExistsInDeployedSchema_ReturnsProbeFailed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.DoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ProbeFailed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnToColumnProbeAgainstUndeployedTable_ReturnsProbeFailed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            ColumnOperand("dbo.AlsoDoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ProbeFailed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownVerdictCausedByUnresolvedCollation_NeverConfirmedEvenThoughTheColumnActuallyConverts()
    {
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownVerdict_NeverAttemptsAProbeAtAll()
    {
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.DoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnOnATempTable_ProbesSuccessfullyRatherThanProbeFailed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("#TraceStatus", "TraceFlag", new SqlType(SqlTypeCategory.VarChar, Length: 10)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedUnindexed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnOnAnInlineTableValuedFunction_ProbesSuccessfullyRatherThanProbeFailed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.SplitStrings_CTE", "Item", new SqlType(SqlTypeCategory.NVarChar, Length: 4000)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedUnindexed, result.Outcome);
    }
}
