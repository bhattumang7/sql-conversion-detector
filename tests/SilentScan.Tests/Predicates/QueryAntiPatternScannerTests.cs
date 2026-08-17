using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §B "Query anti-patterns
/// still unbuilt" - fire/near-miss coverage for every <see cref="QueryAntiPatternFindingKind"/>.
/// See <see cref="QueryAntiPatternFinding"/> for each kind's own scope/precision story and oracle
/// evidence.
/// </summary>
public sealed class QueryAntiPatternScannerTests
{
    private const string Ddl =
        "CREATE TABLE dbo.Big (Id INT NOT NULL PRIMARY KEY, Col VARCHAR(20) NOT NULL);"
        + "CREATE TABLE dbo.A (Id INT NOT NULL PRIMARY KEY);"
        + "CREATE TABLE dbo.B (Id INT NOT NULL PRIMARY KEY, AId INT NOT NULL);"
        + "CREATE UNIQUE INDEX UX_B_AId ON dbo.B(AId);"
        + "CREATE TABLE dbo.C (Id INT NOT NULL PRIMARY KEY, AId INT NOT NULL);";

    private static IReadOnlyList<QueryAntiPatternFinding> Scan(string sql, int? compatibilityLevel = null)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.CompatibilityLevel = compatibilityLevel;
        return QueryAntiPatternScanner.Scan(result, catalog);
    }

    // --- TableVariableLowCompatEstimate -----------------------------------------------------

    [Fact]
    public void TableVariableAsJoinSource_BelowCompat150_Fires()
    {
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); INSERT INTO @t SELECT Id FROM dbo.Big; "
            + "SELECT b.Id FROM dbo.Big b JOIN @t t ON b.Id = t.Id;",
            compatibilityLevel: 130);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableLowCompatEstimate);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void TableVariableAsJoinSource_AtCompat150_NeverFiresLowCompatKind()
    {
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); INSERT INTO @t SELECT Id FROM dbo.Big; "
            + "SELECT b.Id FROM dbo.Big b JOIN @t t ON b.Id = t.Id;",
            compatibilityLevel: 150);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableLowCompatEstimate);
    }

    [Fact]
    public void TableVariableAsJoinSource_UnknownCompat_NeverFiresLowCompatKind()
    {
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); INSERT INTO @t SELECT Id FROM dbo.Big; "
            + "SELECT b.Id FROM dbo.Big b JOIN @t t ON b.Id = t.Id;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableLowCompatEstimate);
    }

    // --- TableVariableStaleEstimateInLoop ----------------------------------------------------

    [Fact]
    public void TableVariableReadAndWrittenInSameLoop_UnknownCompat_Fires()
    {
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); DECLARE @i INT = 0; DECLARE @c INT; "
            + "WHILE @i < 5 BEGIN "
            + "INSERT INTO @t SELECT Id FROM dbo.Big WHERE Id = @i; "
            + "SELECT @c = COUNT(Id) FROM @t; "
            + "SET @i = @i + 1; END;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void TableVariableReadOnlyNoWriteInLoop_NeverFires()
    {
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); INSERT INTO @t SELECT Id FROM dbo.Big; "
            + "DECLARE @i INT = 0; DECLARE @c INT; "
            + "WHILE @i < 5 BEGIN SELECT @c = COUNT(Id) FROM @t; SET @i = @i + 1; END;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop);
    }

    [Fact]
    public void TableVariableReadAndWrittenInSameLoop_BelowCompat150_ReportsOnlyLowCompatKind()
    {
        // Below compat 150 the stronger, always-1-row claim already covers the same site - the
        // stale-in-loop kind deliberately declines to double-report it.
        var findings = Scan(
            "DECLARE @t TABLE (Id INT); DECLARE @i INT = 0; DECLARE @c INT; "
            + "WHILE @i < 5 BEGIN "
            + "INSERT INTO @t SELECT Id FROM dbo.Big WHERE Id = @i; "
            + "SELECT @c = COUNT(Id) FROM @t; "
            + "SET @i = @i + 1; END;",
            compatibilityLevel: 130);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop);
    }

    // --- RbarSingleRowLoopDml -----------------------------------------------------------------

    [Fact]
    public void WhileLoopUpdateKeyedToLoopVariable_Fires()
    {
        var findings = Scan(
            "DECLARE @i INT = 0; "
            + "WHILE @i < 100 BEGIN "
            + "UPDATE dbo.Big SET Col = 'x' WHERE Id = @i; "
            + "SET @i = @i + 1; END;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.RbarSingleRowLoopDml);
        Assert.Contains("Id", finding.DetailText);
    }

    [Fact]
    public void WhileLoopDeleteKeyedToCursorFetchedVariable_Fires()
    {
        var findings = Scan(
            "DECLARE @id INT; DECLARE cur CURSOR LOCAL FOR SELECT Id FROM dbo.Big; "
            + "OPEN cur; FETCH NEXT FROM cur INTO @id; "
            + "WHILE @@FETCH_STATUS = 0 BEGIN "
            + "DELETE FROM dbo.Big WHERE Id = @id; "
            + "FETCH NEXT FROM cur INTO @id; END; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.RbarSingleRowLoopDml);
    }

    [Fact]
    public void WhileLoopUpdateWithCompositePredicate_NeverFires()
    {
        var findings = Scan(
            "DECLARE @i INT = 0; "
            + "WHILE @i < 100 BEGIN "
            + "UPDATE dbo.Big SET Col = 'x' WHERE Id = @i AND Col = 'y'; "
            + "SET @i = @i + 1; END;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.RbarSingleRowLoopDml);
    }

    [Fact]
    public void UpdateOutsideAnyLoop_NeverFires()
    {
        var findings = Scan("DECLARE @i INT = 1; UPDATE dbo.Big SET Col = 'x' WHERE Id = @i;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.RbarSingleRowLoopDml);
    }

    // --- GlobalCursorDeclaration ---------------------------------------------------------------

    [Fact]
    public void CursorDeclaredWithoutLocal_Fires()
    {
        var findings = Scan("DECLARE cur CURSOR FOR SELECT Id FROM dbo.Big; OPEN cur; CLOSE cur; DEALLOCATE cur;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GlobalCursorDeclaration);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void CursorDeclaredExplicitGlobal_Fires()
    {
        var findings = Scan("DECLARE cur CURSOR GLOBAL FOR SELECT Id FROM dbo.Big; OPEN cur; CLOSE cur; DEALLOCATE cur;");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.GlobalCursorDeclaration);
    }

    [Fact]
    public void CursorDeclaredLocal_NeverFires()
    {
        var findings = Scan("DECLARE cur CURSOR LOCAL FOR SELECT Id FROM dbo.Big; OPEN cur; CLOSE cur; DEALLOCATE cur;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.GlobalCursorDeclaration);
    }

    // --- CountStarVariableExistenceCheck --------------------------------------------------------

    [Fact]
    public void CountStarAssignedThenComparedToZeroInNextStatement_Fires()
    {
        var findings = Scan(
            "DECLARE @cnt INT; SELECT @cnt = COUNT(*) FROM dbo.Big WHERE Col = 'x'; IF @cnt > 0 SELECT 1;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Theory]
    [InlineData("IF @cnt >= 1 SELECT 1;")]
    [InlineData("IF @cnt = 0 SELECT 1;")]
    [InlineData("IF @cnt <> 0 SELECT 1;")]
    [InlineData("IF 0 = @cnt SELECT 1;")]
    [InlineData("IF 0 < @cnt SELECT 1;")]
    public void CountStarAssignedThenComparedToZero_VariousForms_Fire(string ifStatement)
    {
        var findings = Scan($"DECLARE @cnt INT; SELECT @cnt = COUNT(*) FROM dbo.Big; {ifStatement}");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);
    }

    [Fact]
    public void InlineCountStarScalarSubquery_NeverFires()
    {
        // Oracle-confirmed the optimizer already rewrites this form into an EXISTS-equivalent
        // short-circuiting plan - flagging it would be a false claim.
        var findings = Scan("IF (SELECT COUNT(*) FROM dbo.Big WHERE Col = 'x') > 0 SELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);
    }

    [Fact]
    public void CountStarAssignedButNotComparedInVeryNextStatement_NeverFires()
    {
        var findings = Scan(
            "DECLARE @cnt INT; SELECT @cnt = COUNT(*) FROM dbo.Big; PRINT 'x'; IF @cnt > 0 SELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);
    }

    [Fact]
    public void CountStarAssignedThenUsedForItsMagnitude_NeverFires()
    {
        var findings = Scan(
            "DECLARE @cnt INT; SELECT @cnt = COUNT(*) FROM dbo.Big; PRINT @cnt;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.CountStarVariableExistenceCheck);
    }

    // --- NonAggregateHavingPredicate -----------------------------------------------------------

    [Fact]
    public void HavingConditionOnGroupByKeyOnly_Fires()
    {
        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING Col = 'x';");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void HavingConditionOnAggregateResult_NeverFires()
    {
        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING COUNT(*) > 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
    }

    [Fact]
    public void HavingConditionMixingKeyAndAggregate_FiresForTheKeyOnlyBranch()
    {
        // A conjunctive HAVING can be split at its own AND boundary - the Col = 'x' branch alone
        // is still a correct, independent move to WHERE regardless of the aggregate sibling.
        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING Col = 'x' AND COUNT(*) > 1;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
        Assert.Contains("GROUP BY key", finding.DetailText);
    }

    [Fact]
    public void HavingConditionOredWithAggregate_NeverFires()
    {
        // Never descends through OR - a condition reachable only through an OR branch does not
        // unconditionally qualify (the same AND-only discipline NonUniqueUpdateSourceScanner uses).
        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING Col = 'x' OR COUNT(*) > 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
    }

    // --- UnionOfProvablyDisjointBranches --------------------------------------------------------

    [Fact]
    public void UnionOfTwoDistinctLiteralEqualityBranches_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Big WHERE Col = 'a' UNION SELECT * FROM dbo.Big WHERE Col = 'b';");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void UnionOfThreeDistinctLiteralEqualityBranches_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Big WHERE Col = 'a' "
            + "UNION SELECT * FROM dbo.Big WHERE Col = 'b' "
            + "UNION SELECT * FROM dbo.Big WHERE Col = 'c';");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);
    }

    [Fact]
    public void UnionAll_NeverFires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Big WHERE Col = 'a' UNION ALL SELECT * FROM dbo.Big WHERE Col = 'b';");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);
    }

    [Fact]
    public void UnionWithOverlappingLiteral_NeverFires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Big WHERE Col = 'a' UNION SELECT * FROM dbo.Big WHERE Col = 'a';");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);
    }

    [Fact]
    public void UnionWithJoinBranch_NeverFires()
    {
        var findings = Scan(
            "SELECT a.Id FROM dbo.A a JOIN dbo.B b ON a.Id = b.AId WHERE a.Id = 1 "
            + "UNION SELECT Id FROM dbo.A WHERE Id = 2;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);
    }

    // --- DistinctMaskingJoinFanout ---------------------------------------------------------------

    [Fact]
    public void SelectDistinctJoinOnNonUniqueColumn_Fires()
    {
        var findings = Scan("SELECT DISTINCT a.Id FROM dbo.A a JOIN dbo.C c ON a.Id = c.AId;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.DistinctMaskingJoinFanout);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void SelectDistinctJoinOnUniqueIndexedColumn_NeverFires()
    {
        var findings = Scan("SELECT DISTINCT a.Id FROM dbo.A a JOIN dbo.B b ON a.Id = b.AId;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.DistinctMaskingJoinFanout);
    }

    [Fact]
    public void PlainSelectJoinOnNonUniqueColumn_NoDistinct_NeverFires()
    {
        var findings = Scan("SELECT a.Id FROM dbo.A a JOIN dbo.C c ON a.Id = c.AId;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.DistinctMaskingJoinFanout);
    }

    // --- UnqualifiedTableReference ----------------------------------------------------------------

    [Fact]
    public void UnqualifiedTableReferenceResolvingToRealTable_Fires()
    {
        var findings = Scan("SELECT Id FROM Big WHERE Id = 1;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.UnqualifiedTableReference);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void QualifiedTableReference_NeverFiresUnqualified()
    {
        var findings = Scan("SELECT Id FROM dbo.Big WHERE Id = 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnqualifiedTableReference);
    }

    [Fact]
    public void UnqualifiedCteReference_NeverFiresUnqualified()
    {
        var findings = Scan(
            "WITH Big AS (SELECT Id FROM dbo.A) SELECT Id FROM Big;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnqualifiedTableReference);
    }

    [Fact]
    public void UnqualifiedTempTableReference_NeverFires()
    {
        var findings = Scan("CREATE TABLE #t (Id INT); SELECT Id FROM #t;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnqualifiedTableReference);
    }

    [Fact]
    public void UnqualifiedReferenceToNonexistentTable_NeverFires()
    {
        var findings = Scan("SELECT Id FROM NoSuchTable WHERE Id = 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnqualifiedTableReference);
    }

    // --- MERGE hazards: MergeMissingHoldlock / MergeNonUniqueUsingSource / MergeUnconditionalDelete -

    [Fact]
    public void MergeTargetWithNoHoldlockHint_Fires()
    {
        var findings = Scan(
            "MERGE dbo.A AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN MATCHED THEN UPDATE SET t.Id = t.Id "
            + "WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.AId);");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeMissingHoldlock);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void MergeTargetWithHoldlockHint_NeverFires()
    {
        var findings = Scan(
            "MERGE dbo.A WITH (HOLDLOCK) AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN MATCHED THEN UPDATE SET t.Id = t.Id "
            + "WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.AId);");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeMissingHoldlock);
    }

    [Fact]
    public void MergeUsingNonUniqueSource_Fires()
    {
        var findings = Scan(
            "MERGE dbo.A AS t USING dbo.C AS s ON t.Id = s.AId "
            + "WHEN MATCHED THEN UPDATE SET t.Id = t.Id "
            + "WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.AId);");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeNonUniqueUsingSource);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void MergeUsingUniqueBackedSource_NeverFires()
    {
        var findings = Scan(
            "MERGE dbo.A AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN MATCHED THEN UPDATE SET t.Id = t.Id "
            + "WHEN NOT MATCHED THEN INSERT (Id) VALUES (s.AId);");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeNonUniqueUsingSource);
    }

    [Fact]
    public void MergeUnconditionalWhenMatchedDelete_Fires()
    {
        var findings = Scan(
            "MERGE dbo.A WITH (HOLDLOCK) AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN MATCHED THEN DELETE;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeUnconditionalDelete);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void MergeUnconditionalWhenNotMatchedBySourceDelete_Fires()
    {
        var findings = Scan(
            "MERGE dbo.A WITH (HOLDLOCK) AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN NOT MATCHED BY SOURCE THEN DELETE;");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeUnconditionalDelete);
    }

    [Fact]
    public void MergeConditionallyQualifiedDelete_NeverFires()
    {
        var findings = Scan(
            "MERGE dbo.A WITH (HOLDLOCK) AS t USING dbo.B AS s ON t.Id = s.AId "
            + "WHEN MATCHED AND s.AId > 0 THEN DELETE;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MergeUnconditionalDelete);
    }

    // --- RecursiveCteMissingMaxRecursion ------------------------------------------------------------

    [Fact]
    public void RecursiveCteWithNoMaxRecursionOption_Fires()
    {
        var findings = Scan(
            "WITH r AS (SELECT Id FROM dbo.A WHERE Id = 1 UNION ALL SELECT a.Id FROM dbo.A a JOIN r ON a.Id = r.Id + 1) "
            + "SELECT Id FROM r;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void RecursiveCteWithMaxRecursionOption_NeverFires()
    {
        var findings = Scan(
            "WITH r AS (SELECT Id FROM dbo.A WHERE Id = 1 UNION ALL SELECT a.Id FROM dbo.A a JOIN r ON a.Id = r.Id + 1) "
            + "SELECT Id FROM r OPTION (MAXRECURSION 500);");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion);
    }

    [Fact]
    public void NonRecursiveCte_NeverFiresMaxRecursion()
    {
        var findings = Scan(
            "WITH r AS (SELECT Id FROM dbo.A) SELECT Id FROM r;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion);
    }

    // --- UnboundedTableWrite -------------------------------------------------------------------

    [Fact]
    public void UpdateWithNoWhereNoTop_Fires()
    {
        var findings = Scan("UPDATE dbo.Big SET Col = 'x';");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.UnboundedTableWrite);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void DeleteWithNoWhereNoTop_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Big;");

        Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.UnboundedTableWrite);
    }

    [Fact]
    public void UpdateWithWhere_NeverFires()
    {
        var findings = Scan("UPDATE dbo.Big SET Col = 'x' WHERE Id = 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnboundedTableWrite);
    }

    [Fact]
    public void DeleteWithTop_NeverFires()
    {
        var findings = Scan("DELETE TOP (10) FROM dbo.Big;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.UnboundedTableWrite);
    }

    // --- LinkedServerOrCrossDatabaseReference -------------------------------------------------

    [Fact]
    public void FourPartLinkedServerReference_Fires()
    {
        var findings = Scan("SELECT Id FROM RemoteServer.RemoteDb.dbo.RemoteTable;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void ThreePartReference_FileMode_NeverFires()
    {
        // File mode has no known "current database" to compare against - never guessed.
        var findings = Scan("SELECT Id FROM OtherDb.dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);
    }

    [Fact]
    public void ThreePartReference_LiveMode_DifferentDatabase_Fires()
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\nSELECT Id FROM OtherDb.dbo.T;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        catalog.CurrentDatabaseName = "ThisDb";

        var findings = QueryAntiPatternScanner.Scan(result, catalog);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void ThreePartReference_LiveMode_SystemDatabase_NeverFires()
    {
        // master/tempdb/msdb/model - overwhelmingly a metadata/catalog-view read in real corpus
        // code, not a genuine cross-database business predicate - excluded on purpose.
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\nSELECT object_id FROM tempdb.sys.objects;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        catalog.CurrentDatabaseName = "ThisDb";

        var findings = QueryAntiPatternScanner.Scan(result, catalog);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);
    }

    [Fact]
    public void ThreePartReference_LiveMode_SameDatabase_NeverFires()
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\nSELECT Id FROM ThisDb.dbo.Big;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        catalog.CurrentDatabaseName = "ThisDb";

        var findings = QueryAntiPatternScanner.Scan(result, catalog);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);
    }
}
