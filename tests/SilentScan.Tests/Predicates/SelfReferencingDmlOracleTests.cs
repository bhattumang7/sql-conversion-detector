using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Halloween Protection and self-referencing DML" - oracle-
/// confirms the underlying plan-shape claim across all four statement kinds, compile-only (a self-
/// referencing DML's defensive plan work is a compile-time structural artifact, not a cardinality-
/// dependent choice - confirmed directly against completely empty tables).
///
/// <b>Corrects the checklist's own "forces a blocking eager spool" premise:</b> INSERT/DELETE
/// really do gain a <c>PhysicalOp="Table Spool" LogicalOp="Eager Spool"</c> operator, but
/// UPDATE ... FROM self-join and MERGE gain a <c>Sort</c> instead - no spool at all. Both are
/// absent from the otherwise-identical cross-table control in every pair below, so "the read side
/// re-reads the write target" reliably predicts SOME extra defensive plan work, even though which
/// operator appears depends on the statement's own shape - see
/// <see cref="Predicates.SelfReferencingDmlFinding"/>'s own doc comment for the full write-up.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class SelfReferencingDmlOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SelfReferencingDmlOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Val INT NOT NULL, Flag BIT NOT NULL DEFAULT 0);
        GO
        CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, RefId INT NOT NULL);
        GO
        CREATE VIEW dbo.vT AS SELECT Id, Val, Flag FROM dbo.T;
        GO
        """;

    private Task<string> CaptureAsync(string probe) => new PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

    [Fact]
    public async Task InsertHoleFillingSelfReference_GainsAnEagerSpool()
    {
        var planXml = await CaptureAsync(
            """
            INSERT INTO dbo.T (Id, Val, Flag)
            SELECT Id + 1000, Val, 0 FROM dbo.T WHERE NOT EXISTS (SELECT 1 FROM dbo.T t2 WHERE t2.Id = dbo.T.Id + 1000);
            """);

        Assert.Contains("LogicalOp=\"Eager Spool\"", planXml);
    }

    [Fact]
    public async Task InsertFromDifferentTable_NeverGainsAnEagerSpool()
    {
        var planXml = await CaptureAsync(
            "INSERT INTO dbo.T (Id, Val, Flag) SELECT Id + 5000, RefId, 0 FROM dbo.Other;");

        Assert.DoesNotContain("LogicalOp=\"Eager Spool\"", planXml);
    }

    [Fact]
    public async Task InsertReadingThroughAViewOverTheSameBaseTable_StillGainsAnEagerSpool()
    {
        var planXml = await CaptureAsync(
            """
            INSERT INTO dbo.T (Id, Val, Flag)
            SELECT Id + 2000, Val, 0 FROM dbo.vT WHERE NOT EXISTS (SELECT 1 FROM dbo.vT v2 WHERE v2.Id = dbo.vT.Id + 2000);
            """);

        Assert.Contains("LogicalOp=\"Eager Spool\"", planXml);
    }

    [Fact]
    public async Task UpdateFromSelfJoin_GainsAProtectiveSort_NoSpool()
    {
        var planXml = await CaptureAsync(
            "UPDATE t1 SET t1.Val = t2.Val FROM dbo.T t1 JOIN dbo.T t2 ON t1.Id = t2.Id - 1;");

        Assert.Contains("LogicalOp=\"Distinct Sort\"", planXml);
        Assert.DoesNotContain("Eager Spool", planXml);
    }

    [Fact]
    public async Task UpdateFromJoinToDifferentTable_NeverGainsTheProtectiveSort()
    {
        var planXml = await CaptureAsync(
            "UPDATE t1 SET t1.Val = o.RefId FROM dbo.T t1 JOIN dbo.Other o ON t1.Id = o.Id;");

        Assert.DoesNotContain("Distinct Sort", planXml);
        Assert.DoesNotContain("Eager Spool", planXml);
    }

    [Fact]
    public async Task UpdateWithNoFromClauseAndNoSelfReference_NeverGainsAnyProtectiveOperator()
    {
        // DMLRequestSort="0" is a real, always-present Update-element attribute containing the
        // substring "Sort" - assert on a genuine RelOp/PhysicalOp element, never a bare substring
        // match (the same trap SetOptionOracleTests' own doc comment already documents).
        var planXml = await CaptureAsync("UPDATE dbo.T SET Val = Val + 1 WHERE Flag = 1;");

        Assert.DoesNotContain("PhysicalOp=\"Sort\"", planXml);
        Assert.DoesNotContain("Eager Spool", planXml);
    }

    [Fact]
    public async Task DeleteWhereExistsSelfReference_GainsAnEagerSpool()
    {
        var planXml = await CaptureAsync(
            "DELETE FROM dbo.T WHERE EXISTS (SELECT 1 FROM dbo.T t2 WHERE t2.Id = T.Id - 1);");

        Assert.Contains("LogicalOp=\"Eager Spool\"", planXml);
    }

    [Fact]
    public async Task DeleteWhereExistsDifferentTable_NeverGainsAnEagerSpool()
    {
        var planXml = await CaptureAsync(
            "DELETE FROM dbo.T WHERE EXISTS (SELECT 1 FROM dbo.Other o WHERE o.Id = T.Id);");

        Assert.DoesNotContain("Eager Spool", planXml);
    }

    [Fact]
    public async Task MergeUsingSameTargetTable_GainsAProtectiveSort_NoSpool()
    {
        var planXml = await CaptureAsync(
            """
            MERGE dbo.T AS tgt
            USING (SELECT Id, Val FROM dbo.T) AS src
            ON tgt.Id = src.Id + 1
            WHEN MATCHED THEN UPDATE SET tgt.Val = src.Val
            WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Val, Flag) VALUES (src.Id + 1, src.Val, 0);
            """);

        Assert.Contains("PhysicalOp=\"Sort\"", planXml);
        Assert.DoesNotContain("Eager Spool", planXml);
    }

    [Fact]
    public async Task MergeUsingDifferentSourceTable_NeverGainsTheProtectiveSort()
    {
        var planXml = await CaptureAsync(
            """
            MERGE dbo.T AS tgt
            USING (SELECT Id, RefId AS Val FROM dbo.Other) AS src
            ON tgt.Id = src.Id
            WHEN MATCHED THEN UPDATE SET tgt.Val = src.Val
            WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Val, Flag) VALUES (src.Id, src.Val, 0);
            """);

        Assert.DoesNotContain("PhysicalOp=\"Sort\"", planXml);
        Assert.DoesNotContain("Eager Spool", planXml);
    }
}
