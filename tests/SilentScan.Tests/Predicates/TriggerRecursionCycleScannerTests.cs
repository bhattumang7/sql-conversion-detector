using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep (2026-08-18)" §G "Multi-hop
/// trigger recursion cycle across tables" - fire/near-miss coverage for
/// <see cref="TriggerRecursionCycleFinding"/>. See that finding's own doc comment for the full
/// precision story, the gating correction (server-level 'nested triggers', not database-level
/// RECURSIVE_TRIGGERS), and the real Docker oracle evidence (a disposable scratch database and a
/// real cross-table trigger cascade, dropped immediately after - not reproduced here, this file
/// exercises the static AST+catalog claim only).
/// </summary>
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
        // File-mode/never-read - never overclaim a risk that may not be live.
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
        // A writes to B, B writes to C, but nothing writes back to A - no cycle exists at all.
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
        // A single trigger writing back to its own table is DirectRecursiveTrigger's own claim
        // (TriggerCorrectnessScanner), not this cross-table stream.
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_TA ON dbo.TA AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.TA SET Id = Id WHERE Id = 1; END;",
            nestedTriggersEnabled: true);

        Assert.Empty(findings);
    }
}
