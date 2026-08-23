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
}
