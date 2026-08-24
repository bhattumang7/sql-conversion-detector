using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class CrossModuleLockOrderScannerTests
{
    private const string Ddl =
        "CREATE TABLE dbo.T1 (Id INT NOT NULL PRIMARY KEY);"
        + "CREATE TABLE dbo.T2 (Id INT NOT NULL PRIMARY KEY);";

    private static IReadOnlyList<CrossModuleLockOrderFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return CrossModuleLockOrderScanner.Scan([result], catalog);
    }

    [Fact]
    public void TwoProceduresWriteSameTablesInOppositeOrder_BothInsideExplicitTransactions_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void TwoProceduresWriteSameTablesInSameOrder_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureWritesRealTableThenAWriteableCteSharingAnotherTablesName_NeverMisattributedAsConflict()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + ";WITH T1 AS (SELECT Id FROM dbo.T2) UPDATE T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OppositeOrderWrites_OutsideAnyExplicitTransaction_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OppositeOrderWrites_OneProcedureOnlyWritesOneOfTheTwoTables_NeverFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OppositeOrderWrites_ThroughTableVariable_NeverFires()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "DECLARE @t TABLE (Id INT); "
            + "BEGIN TRANSACTION; "
            + "UPDATE @t SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "DECLARE @t TABLE (Id INT); "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE @t SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnbalancedExtraCommit_DoesNotCorruptTransactionDepthForLaterWrites()
    {

        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "COMMIT TRANSACTION; "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public void SingleProcedureWritingBothTables_NeverFiresAgainstItself()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void AlteredAndCreateOrAlterProcedures_StillCompared_OppositeOrderFires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "ALTER PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE OR ALTER PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f =>
        {
            Assert.Equal("dbo.T1", f.FirstTableQualifiedName);
            Assert.Equal("dbo.T2", f.SecondTableQualifiedName);
            Assert.Equal("dbo.ProcA", f.FirstTableFirstOrdering.ProcedureQualifiedName);
            Assert.Equal("dbo.ProcB", f.SecondTableFirstOrdering.ProcedureQualifiedName);
        });
    }

    [Fact]
    public void TwoDefinitionsOfSameProcedureName_NeverComparedAgainstEachOther()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnbalancedExtraRollback_DoesNotCorruptTransactionDepthForLaterWrites()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "ROLLBACK TRANSACTION; "
            + "ROLLBACK TRANSACTION; "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public void InsertAndDeleteWritesInsideTransaction_CountTowardLockOrder()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "INSERT INTO dbo.T1 (Id) VALUES (1); "
            + "DELETE FROM dbo.T2 WHERE Id = 1; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public void MergeStatementWrite_CountsTowardLockOrder()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "MERGE INTO dbo.T1 AS tgt USING (SELECT 1 AS Id) AS src ON tgt.Id = src.Id "
            + "WHEN MATCHED THEN UPDATE SET tgt.Id = src.Id "
            + "WHEN NOT MATCHED THEN INSERT (Id) VALUES (src.Id); "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public void FirstProcedureWritesHigherNamedTableFirst_FindingStillAttributesCorrectProcedurePerTable()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.ProcA AS BEGIN\n"
            + "BEGIN TRANSACTION;\n"
            + "UPDATE dbo.T2 SET Id = Id;\n"
            + "UPDATE dbo.T1 SET Id = Id;\n"
            + "COMMIT TRANSACTION;\n"
            + "END;\n"
            + "GO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN\n"
            + "BEGIN TRANSACTION;\n"
            + "UPDATE dbo.T1 SET Id = Id;\n"
            + "UPDATE dbo.T2 SET Id = Id;\n"
            + "COMMIT TRANSACTION;\n"
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcB", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal(12, finding.FirstTableFirstOrdering.FirstWriteLine);
        Assert.Equal(13, finding.FirstTableFirstOrdering.SecondWriteLine);
        Assert.Equal("dbo.ProcA", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal(6, finding.SecondTableFirstOrdering.FirstWriteLine);
        Assert.Equal(5, finding.SecondTableFirstOrdering.SecondWriteLine);
    }

    [Fact]
    public void SynonymForATable_ResolvedToSameQualifiedNameAsDirectReference()
    {
        var findings = Scan(
            "CREATE SYNONYM dbo.SynT2 FOR dbo.T2;"
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcA AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "UPDATE dbo.SynT2 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END; "
            + "\nGO\n"
            + "CREATE PROCEDURE dbo.ProcB AS BEGIN "
            + "BEGIN TRANSACTION; "
            + "UPDATE dbo.T2 SET Id = Id; "
            + "UPDATE dbo.T1 SET Id = Id; "
            + "COMMIT TRANSACTION; "
            + "END;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }
}
