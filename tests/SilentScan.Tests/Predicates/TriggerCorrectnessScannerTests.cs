using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §C "Trigger correctness" -
/// fire/near-miss coverage for every <see cref="TriggerCorrectnessFindingKind"/>. See
/// <see cref="TriggerCorrectnessFinding"/> for each kind's own precision story and the real Docker
/// oracle evidence (a disposable scratch database, dropped immediately after - not reproduced
/// here, this file exercises the static AST+catalog claim only).
/// </summary>
public sealed class TriggerCorrectnessScannerTests
{
    private const string Ddl =
        "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Val INT NOT NULL);"
        + "CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, Val INT NOT NULL);";

    private static IReadOnlyList<TriggerCorrectnessFinding> Scan(string triggerSql, bool? recursiveTriggersEnabled = null)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{triggerSql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.IsRecursiveTriggersEnabled = recursiveTriggersEnabled;
        return TriggerCorrectnessScanner.Scan(result, catalog);
    }

    // --- MultiRowUnsafeSingleRowAssignment / MultiRowUnsafeKeyedDml -------------------------

    [Fact]
    public void SelectSetVariableFromInserted_NoWhereNoTopNoAggregate_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN DECLARE @v INT; SELECT @v = Val FROM inserted; PRINT @v; END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment);
        Assert.Equal("dbo.trg_T", finding.TriggerQualifiedName);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void ScalarSubqueryOverDeleted_NoWhereNoTopNoAggregate_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER DELETE AS "
            + "BEGIN DECLARE @v INT = (SELECT Val FROM deleted); PRINT @v; END;");

        Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment);
    }

    [Fact]
    public void SelectSetVariableFromInserted_WithWhere_NeverFires()
    {
        // A WHERE clause narrows to (at most) one row on purpose - not the unsafe shape.
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN DECLARE @v INT; SELECT @v = Val FROM inserted WHERE Id = 1; PRINT @v; END;");

        Assert.DoesNotContain(findings, f => f.Kind is TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment or TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml);
    }

    [Fact]
    public void SelectSetVariableFromInserted_AggregateExpression_NeverFires()
    {
        // COUNT(*)/MAX(...) over the whole rowset is a real, well-defined single value regardless
        // of row count - not the unsafe shape.
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN DECLARE @v INT; SELECT @v = COUNT(*) FROM inserted; PRINT @v; END;");

        Assert.DoesNotContain(findings, f => f.Kind is TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment or TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml);
    }

    [Fact]
    public void UnsafeAssignmentThenStraightLineKeyedUpdate_Fires_SharperKind()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN "
            + "DECLARE @v INT; "
            + "SELECT @v = Val FROM inserted; "
            + "UPDATE dbo.Other SET Val = @v WHERE Id = @v; "
            + "END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        // Never ALSO reported as the general kind for the same site - the sharper kind supersedes it.
        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment);
    }

    [Fact]
    public void UnsafeAssignmentWithNoSubsequentKeyedUse_Fires_GeneralKindOnly()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN "
            + "DECLARE @v INT; "
            + "SELECT @v = Val FROM inserted; "
            + "UPDATE dbo.Other SET Val = 0 WHERE Id = 1; "
            + "END;");

        Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment);
        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml);
    }

    // --- NoEarlyOutForEmptyInvocation ---------------------------------------------------------

    [Fact]
    public void TriggerBodyWithNoGuard_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.Other SET Val = 1 WHERE Id = 1; END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void TriggerBodyWithRowCountGuard_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN IF @@ROWCOUNT = 0 RETURN; UPDATE dbo.Other SET Val = 1 WHERE Id = 1; END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation);
    }

    [Fact]
    public void TriggerBodyWithNotExistsGuard_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN IF NOT EXISTS (SELECT * FROM inserted) RETURN; UPDATE dbo.Other SET Val = 1 WHERE Id = 1; END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation);
    }

    // --- DirectRecursiveTrigger ---------------------------------------------------------------

    [Fact]
    public void SelfWritingTrigger_RecursiveTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.T(Id, Val) SELECT Id + 1, Val FROM inserted; END;",
            recursiveTriggersEnabled: true);

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void SelfWritingTrigger_RecursiveTriggersOff_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.T(Id, Val) SELECT Id + 1, Val FROM inserted; END;",
            recursiveTriggersEnabled: false);

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void SelfWritingTrigger_RecursiveTriggersUnknown_NeverFires()
    {
        // File-mode/never-read - never overclaim a risk that may not be live.
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.T(Id, Val) SELECT Id + 1, Val FROM inserted; END;",
            recursiveTriggersEnabled: null);

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void TriggerWritingOtherTable_RecursiveTriggersOn_NeverFires()
    {
        // A write to a DIFFERENT table is not direct self-recursion, regardless of the option.
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.Other(Id, Val) SELECT Id, Val FROM inserted; END;",
            recursiveTriggersEnabled: true);

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }
}
