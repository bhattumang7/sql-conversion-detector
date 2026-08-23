using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN DECLARE @v INT; SELECT @v = Val FROM inserted WHERE Id = 1; PRINT @v; END;");

        Assert.DoesNotContain(findings, f => f.Kind is TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment or TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml);
    }

    [Fact]
    public void SelectSetVariableFromInserted_AggregateExpression_NeverFires()
    {

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

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.T(Id, Val) SELECT Id + 1, Val FROM inserted; END;",
            recursiveTriggersEnabled: null);

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void TriggerWritingOtherTable_RecursiveTriggersOn_NeverFires()
    {

        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN INSERT INTO dbo.Other(Id, Val) SELECT Id, Val FROM inserted; END;",
            recursiveTriggersEnabled: true);

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void SelfUpdatingTrigger_RecursiveTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN UPDATE dbo.T SET Val = Val + 1 WHERE Id IN (SELECT Id FROM inserted); END;",
            recursiveTriggersEnabled: true);

        Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void SelfDeletingTrigger_RecursiveTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER DELETE AS "
            + "BEGIN DELETE FROM dbo.T WHERE Id IN (SELECT Id FROM deleted); END;",
            recursiveTriggersEnabled: true);

        Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void SelfMergingTrigger_RecursiveTriggersOn_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS "
            + "BEGIN "
            + "MERGE INTO dbo.T AS tgt USING inserted AS src ON tgt.Id = src.Id "
            + "WHEN MATCHED THEN UPDATE SET tgt.Val = src.Val "
            + "WHEN NOT MATCHED THEN INSERT (Id, Val) VALUES (src.Id, src.Val); "
            + "END;",
            recursiveTriggersEnabled: true);

        Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.DirectRecursiveTrigger);
    }

    [Fact]
    public void InsteadOfInsertTrigger_ReinsertsFilteredSubsetWithNoCompanionOrErrorPath_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T INSTEAD OF INSERT AS "
            + "BEGIN INSERT INTO dbo.T (Id, Val) SELECT Id, Val FROM inserted WHERE Val > 0; END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void InsteadOfInsertTrigger_FilteredReinsertWithCompanionInsert_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T INSTEAD OF INSERT AS "
            + "BEGIN "
            + "INSERT INTO dbo.T (Id, Val) SELECT Id, Val FROM inserted WHERE Val > 0; "
            + "INSERT INTO dbo.Other (Id, Val) VALUES (-1, -1); "
            + "END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath);
    }

    [Fact]
    public void InsteadOfInsertTrigger_FilteredReinsertWithRaiserror_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T INSTEAD OF INSERT AS "
            + "BEGIN "
            + "INSERT INTO dbo.T (Id, Val) SELECT Id, Val FROM inserted WHERE Val > 0; "
            + "IF EXISTS (SELECT 1 FROM inserted WHERE Val <= 0) RAISERROR('rejected rows', 16, 1); "
            + "END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath);
    }

    [Fact]
    public void InsteadOfInsertTrigger_UnfilteredReinsertOfAllInsertedRows_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T INSTEAD OF INSERT AS "
            + "BEGIN INSERT INTO dbo.T (Id, Val) SELECT Id, Val FROM inserted; END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath);
    }

    [Fact]
    public void UpdateFunctionGate_NoValueComparison_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN IF UPDATE(Val) UPDATE dbo.Other SET Val = 1 WHERE Id = 1; END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("Val", finding.DetailText);
    }

    [Fact]
    public void UpdateFunctionGate_WithSameColumnInsertedDeletedComparison_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_T ON dbo.T AFTER UPDATE AS "
            + "BEGIN "
            + "IF UPDATE(Val) AND EXISTS (SELECT 1 FROM inserted i INNER JOIN deleted d ON i.Id = d.Id WHERE i.Val <> d.Val) "
            + "UPDATE dbo.Other SET Val = 1 WHERE Id = 1; "
            + "END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison);
    }

    [Fact]
    public void LogonTrigger_HostNameGateWithRollback_Fires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_HostGate ON ALL SERVER FOR LOGON AS "
            + "BEGIN IF HOST_NAME() NOT IN ('APPSERVER01') ROLLBACK; END;");

        var finding = Assert.Single(findings, f => f.Kind == TriggerCorrectnessFindingKind.LogonTriggerHostNameGate);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void LogonTrigger_HostNameCheckedButNoRollback_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_HostGate ON ALL SERVER FOR LOGON AS "
            + "BEGIN IF HOST_NAME() NOT IN ('APPSERVER01') PRINT 'unexpected host'; END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.LogonTriggerHostNameGate);
    }

    [Fact]
    public void LogonTrigger_RollbackWithoutHostNameCheck_NeverFires()
    {
        var findings = Scan(
            "CREATE TRIGGER dbo.trg_HostGate ON ALL SERVER FOR LOGON AS "
            + "BEGIN IF ORIGINAL_LOGIN() NOT IN ('sa') ROLLBACK; END;");

        Assert.DoesNotContain(findings, f => f.Kind == TriggerCorrectnessFindingKind.LogonTriggerHostNameGate);
    }
}
