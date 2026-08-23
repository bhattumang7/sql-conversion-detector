using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class TriggerRecursionCycleScannerTests
{
    private const string Ddl =
        "CREATE TABLE dbo.TA (Id INT NOT NULL PRIMARY KEY);"
        + "CREATE TABLE dbo.TB (Id INT NOT NULL PRIMARY KEY);"
        + "CREATE TABLE dbo.TC (Id INT NOT NULL PRIMARY KEY);";

    private static IReadOnlyList<TriggerRecursionCycleFinding> Scan(string sql, bool? nestedTriggersEnabled)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.IsNestedTriggersEnabled = nestedTriggersEnabled;
        return TriggerRecursionCycleScanner.Scan([result], catalog);
    }

    [Fact]
    public void TwoTableCycle_NestedTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TB SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TB ON dbo.TB AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: true);

        var finding = Assert.Single(findings);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
        Assert.Equal(2, finding.CycleTableQualifiedNames.Count);
        Assert.Contains("dbo.TA", finding.CycleTableQualifiedNames);
        Assert.Contains("dbo.TB", finding.CycleTableQualifiedNames);
        Assert.Equal(2, finding.Hops.Count);
    }

    [Fact]
    public void TwoTableCycle_NestedTriggersOff_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TB SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TB ON dbo.TB AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void TwoTableCycle_NestedTriggersUnknown_NeverFires()
    {

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TB SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TB ON dbo.TB AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeTableCycle_NestedTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TB SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TB ON dbo.TB AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TC SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TC ON dbo.TC AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: true);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.CycleTableQualifiedNames.Count);
    }

    [Fact]
    public void OneWayChainNoCycle_NeverFires()
    {

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TB SET Id = Id WHERE Id = 1; END;"
            + "\nGO\n"
            + "CREATE TRIGGER dbo.trg_TB ON dbo.TB AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TC SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: true);

        Assert.Empty(findings);
    }

    [Fact]
    public void SelfWritingTriggerAlone_NeverFires_NotThisStream()
    {

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: true);

        Assert.Empty(findings);
    }
}
