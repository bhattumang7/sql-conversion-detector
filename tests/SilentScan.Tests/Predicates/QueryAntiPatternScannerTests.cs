using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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

    [Fact]
    public void TableValuedParameter_AtCompat170_FiresTableVariablePspSkip()
    {
        var findings = Scan(
            "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY);\nGO\n"
            + "CREATE PROCEDURE dbo.P @ids dbo.IdList READONLY AS SELECT 1;",
            compatibilityLevel: 170);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariablePspSkip);
        Assert.Equal("@ids", finding.DetailText);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void TableValuedParameter_BelowCompat170_NeverFiresTableVariablePspSkip()
    {
        var findings = Scan(
            "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL PRIMARY KEY);\nGO\n"
            + "CREATE PROCEDURE dbo.P @ids dbo.IdList READONLY AS SELECT 1;",
            compatibilityLevel: 160);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariablePspSkip);
    }

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

        var findings = Scan(
            "DECLARE @t TABLE (Id INT); DECLARE @i INT = 0; DECLARE @c INT; "
            + "WHILE @i < 5 BEGIN "
            + "INSERT INTO @t SELECT Id FROM dbo.Big WHERE Id = @i; "
            + "SELECT @c = COUNT(Id) FROM @t; "
            + "SET @i = @i + 1; END;",
            compatibilityLevel: 130);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop);
    }

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

        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING Col = 'x' AND COUNT(*) > 1;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
        Assert.Contains("GROUP BY key", finding.DetailText);
    }

    [Fact]
    public void HavingConditionOredWithAggregate_NeverFires()
    {

        var findings = Scan("SELECT Col, COUNT(*) FROM dbo.Big GROUP BY Col HAVING Col = 'x' OR COUNT(*) > 1;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
    }

    [Fact]
    public void HavingConditionInsideUnsatisfiableConjunct_NeverFires()
    {
        var findings = Scan("SELECT Id, COUNT(*) FROM dbo.Big GROUP BY Id HAVING Id = 1 AND Id = 2;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.NonAggregateHavingPredicate);
    }

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

    [Fact]
    public void SelectDistinctJoinOnNonUniqueColumn_WhereClauseUnsatisfiable_NeverFires()
    {
        var findings = Scan("SELECT DISTINCT a.Id FROM dbo.A a JOIN dbo.C c ON a.Id = c.AId WHERE a.Id = 1 AND a.Id = 2;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.DistinctMaskingJoinFanout);
    }

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
    public void MergeUsingNonUniqueSource_OnClauseUnsatisfiable_NeverFires()
    {
        var findings = Scan(
            "MERGE dbo.A AS t USING dbo.C AS s ON t.Id = s.AId AND s.AId = 1 AND s.AId = 2 "
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

    private static DatabaseCatalog CatalogWithCouponTable(bool ignoreDupKey)
    {
        var ddl = "CREATE TABLE dbo.Coupon (Code VARCHAR(20) NOT NULL, Pct INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        var existing = catalog.Find("dbo.Coupon")!;
        var index = new CatalogIndex(
            "UX_Coupon_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"],
            IncludedColumns: [], IgnoreDupKey: ignoreDupKey);
        catalog.AddOrReplace(existing with { Indexes = [index] });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanCoupon(string sql, bool ignoreDupKey)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithCouponTable(ignoreDupKey));
    }

    [Fact]
    public void MultiRowInsert_IntoIgnoreDupKeyUniqueIndex_Fires()
    {
        var findings = ScanCoupon(
            "INSERT INTO dbo.Coupon (Code, Pct) VALUES ('SAVE10', 10), ('SAVE20', 20), ('SAVE10', 15);",
            ignoreDupKey: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("UX_Coupon_Code", finding.DetailText);
    }

    [Fact]
    public void SingleRowInsert_IntoIgnoreDupKeyUniqueIndex_NeverFires()
    {

        var findings = ScanCoupon(
            "INSERT INTO dbo.Coupon (Code, Pct) VALUES ('SAVE10', 10);",
            ignoreDupKey: true);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop);
    }

    [Fact]
    public void MultiRowInsert_IntoOrdinaryUniqueIndex_NeverFires()
    {

        var findings = ScanCoupon(
            "INSERT INTO dbo.Coupon (Code, Pct) VALUES ('SAVE10', 10), ('SAVE20', 20), ('SAVE10', 15);",
            ignoreDupKey: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop);
    }

    [Fact]
    public void MultiRowInsertSelect_IntoIgnoreDupKeyUniqueIndex_NeverFires()
    {

        var findings = ScanCoupon(
            "CREATE TABLE dbo.CouponSource (Code VARCHAR(20) NOT NULL, Pct INT NOT NULL); "
            + "INSERT INTO dbo.Coupon (Code, Pct) SELECT Code, Pct FROM dbo.CouponSource;",
            ignoreDupKey: true);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop);
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitch(string ddl, string switchSql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{switchSql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return QueryAntiPatternScanner.Scan(result, catalog);
    }

    [Fact]
    public void AlterTableSwitch_DifferentColumnCount_Fires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount INT NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4943", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_DifferentColumnNameAtSameOrdinal_Fires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount INT NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Amt INT NOT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Contains("4942", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_DifferentDataType_Fires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount DECIMAL(10,2) NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Amount DECIMAL(12,4) NOT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Contains("4944", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_DifferentNullability_Fires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount INT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Amount INT NOT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Contains("4985", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_ComputedOnOneSideOnly_Fires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount AS (Id * 2)); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Amount INT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Contains("4965", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_IdenticalShape_NeverFires()
    {
        var findings = ScanSwitch(
            "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Amount DECIMAL(10,2) NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);",
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
    }

    private static DatabaseCatalog CatalogWithSwitchTables(
        IReadOnlyList<CatalogIndex> sourceIndexes, IReadOnlyList<CatalogIndex> targetIndexes)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, Code VARCHAR(20) NOT NULL, Pct INT NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, Code VARCHAR(20) NOT NULL, Pct INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { Indexes = sourceIndexes });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { Indexes = targetIndexes });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchIndexes(
        IReadOnlyList<CatalogIndex> sourceIndexes, IReadOnlyList<CatalogIndex> targetIndexes)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchTables(sourceIndexes, targetIndexes));
    }

    [Fact]
    public void AlterTableSwitch_ClusteredIndexPresenceMismatch_Fires()
    {
        var findings = ScanSwitchIndexes(
            sourceIndexes: [],
            targetIndexes: [new CatalogIndex("CX_SwTgt", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Id"], IncludedColumns: [], IsClustered: true)]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4913", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_TargetIndexMissingFromSource_Fires()
    {
        var findings = ScanSwitchIndexes(
            sourceIndexes: [],
            targetIndexes: [new CatalogIndex("IX_SwTgt_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: [])]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
        Assert.Contains("4947", finding.DetailText);
        Assert.Contains("IX_SwTgt_Code", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_TargetIndexIncludeColumnMissingFromSource_Fires()
    {
        var findings = ScanSwitchIndexes(
            sourceIndexes: [new CatalogIndex("IX_SwSrc_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: [])],
            targetIndexes: [new CatalogIndex("IX_SwTgt_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: ["Pct"])]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
        Assert.Contains("4947", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_TargetIndexSortDirectionDiffersFromSource_Fires()
    {
        var findings = ScanSwitchIndexes(
            sourceIndexes: [new CatalogIndex("IX_SwSrc_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: [], KeyColumnIsDescendingRaw: [false])],
            targetIndexes: [new CatalogIndex("IX_SwTgt_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: [], KeyColumnIsDescendingRaw: [true])]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
        Assert.Contains("4947", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceHasExtraIndexTargetLacks_NeverFires()
    {

        var findings = ScanSwitchIndexes(
            sourceIndexes: [new CatalogIndex("IX_SwSrc_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: [])],
            targetIndexes: []);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
    }

    [Fact]
    public void AlterTableSwitch_IdenticalIndexSet_NeverFires()
    {
        var findings = ScanSwitchIndexes(
            sourceIndexes: [new CatalogIndex("IX_SwSrc_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: ["Pct"], KeyColumnIsDescendingRaw: [false])],
            targetIndexes: [new CatalogIndex("IX_SwTgt_Code", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["Code"], IncludedColumns: ["Pct"], KeyColumnIsDescendingRaw: [false])]);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch);
    }

    private static DatabaseCatalog CatalogWithSwitchConstraints(
        IReadOnlyList<CatalogCheckConstraint> checkConstraints, IReadOnlyList<ForeignKeyRelationship> foreignKeys)
    {
        var ddl = "CREATE TABLE dbo.SwRef (Id INT NOT NULL); "
            + "CREATE TABLE dbo.SwSrc (Id INT NOT NULL, RegionId INT NOT NULL); "
            + "CREATE TABLE dbo.SwTgt (Id INT NOT NULL, RegionId INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        foreach (var check in checkConstraints)
        {
            catalog.AddCheckConstraint(check);
        }

        foreach (var fk in foreignKeys)
        {
            catalog.AddForeignKey(fk);
        }

        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchConstraints(
        IReadOnlyList<CatalogCheckConstraint> checkConstraints, IReadOnlyList<ForeignKeyRelationship> foreignKeys)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchConstraints(checkConstraints, foreignKeys));
    }

    [Fact]
    public void AlterTableSwitch_TargetCheckConstraintMissingFromSource_Fires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints: [new CatalogCheckConstraint("CK_SwTgt", "dbo.SwTgt", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([RegionId]>(0))")],
            foreignKeys: []);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4970", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_MatchingCheckConstraintDisabledStateDiffers_Fires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints:
            [
                new CatalogCheckConstraint("CK_SwSrc", "dbo.SwSrc", IsNotTrusted: false, IsDisabled: true, DefinitionText: "([RegionId]>(0))"),
                new CatalogCheckConstraint("CK_SwTgt", "dbo.SwTgt", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([RegionId]>(0))"),
            ],
            foreignKeys: []);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
        Assert.Contains("4960", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceHasExtraCheckConstraintTargetLacks_NeverFires()
    {

        var findings = ScanSwitchConstraints(
            checkConstraints: [new CatalogCheckConstraint("CK_SwSrc", "dbo.SwSrc", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([RegionId]>(0))")],
            foreignKeys: []);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
    }

    [Fact]
    public void AlterTableSwitch_TargetForeignKeyMissingFromSource_Fires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints: [],
            foreignKeys: [new ForeignKeyRelationship("FK_SwTgt", "dbo.SwTgt", "RegionId", "dbo.SwRef", "Id")]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
        Assert.Contains("4968", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_MatchingForeignKeyEnabledStateDiffers_Fires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints: [],
            foreignKeys:
            [
                new ForeignKeyRelationship("FK_SwSrc", "dbo.SwSrc", "RegionId", "dbo.SwRef", "Id", IsDisabled: true),
                new ForeignKeyRelationship("FK_SwTgt", "dbo.SwTgt", "RegionId", "dbo.SwRef", "Id", IsDisabled: false),
            ]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
        Assert.Contains("4969", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_MatchingForeignKeyTrustStateDiffers_Fires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints: [],
            foreignKeys:
            [
                new ForeignKeyRelationship("FK_SwSrc", "dbo.SwSrc", "RegionId", "dbo.SwRef", "Id", IsNotTrusted: true),
                new ForeignKeyRelationship("FK_SwTgt", "dbo.SwTgt", "RegionId", "dbo.SwRef", "Id", IsNotTrusted: false),
            ]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
        Assert.Contains("4974", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_IdenticalConstraints_NeverFires()
    {
        var findings = ScanSwitchConstraints(
            checkConstraints:
            [
                new CatalogCheckConstraint("CK_SwSrc", "dbo.SwSrc", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([RegionId]>(0))"),
                new CatalogCheckConstraint("CK_SwTgt", "dbo.SwTgt", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([RegionId]>(0))"),
            ],
            foreignKeys:
            [
                new ForeignKeyRelationship("FK_SwSrc", "dbo.SwSrc", "RegionId", "dbo.SwRef", "Id"),
                new ForeignKeyRelationship("FK_SwTgt", "dbo.SwTgt", "RegionId", "dbo.SwRef", "Id"),
            ]);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch);
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchTargetOnlyIndexes(
        IReadOnlyList<CatalogIndex> sourceIndexes, IReadOnlyList<CatalogIndex> targetIndexes)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchTables(sourceIndexes, targetIndexes));
    }

    [Fact]
    public void AlterTableSwitch_TargetHasXmlIndex_Fires()
    {
        var findings = ScanSwitchTargetOnlyIndexes(
            sourceIndexes: [],
            targetIndexes: [new CatalogIndex("PXML_SwTgt", CatalogIndexKind.Index, IsUnique: false, KeyColumns: [], IncludedColumns: [], IsXmlIndex: true)]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4983", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceHasXmlIndexTargetDoesNot_NeverFires()
    {

        var findings = ScanSwitchTargetOnlyIndexes(
            sourceIndexes: [new CatalogIndex("PXML_SwSrc", CatalogIndexKind.Index, IsUnique: false, KeyColumns: [], IncludedColumns: [], IsXmlIndex: true)],
            targetIndexes: []);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction);
    }

    [Fact]
    public void AlterTableSwitch_TargetHasSpatialIndex_Fires()
    {
        var findings = ScanSwitchTargetOnlyIndexes(
            sourceIndexes: [],
            targetIndexes: [new CatalogIndex("SIDX_SwTgt", CatalogIndexKind.Index, IsUnique: false, KeyColumns: [], IncludedColumns: [], IsSpatialIndex: true)]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction);
        Assert.Contains("4983", finding.DetailText);
    }

    private static DatabaseCatalog CatalogWithSwitchFullTextIndexes(bool sourceHasFullTextIndex, bool targetHasFullTextIndex)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { HasFullTextIndex = sourceHasFullTextIndex });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { HasFullTextIndex = targetHasFullTextIndex });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchFullTextIndexes(bool sourceHasFullTextIndex, bool targetHasFullTextIndex)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchFullTextIndexes(sourceHasFullTextIndex, targetHasFullTextIndex));
    }

    [Fact]
    public void AlterTableSwitch_SourceHasFullTextIndex_Fires()
    {
        var findings = ScanSwitchFullTextIndexes(sourceHasFullTextIndex: true, targetHasFullTextIndex: false);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4918", finding.DetailText);
        Assert.Contains("dbo.SwSrc", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_TargetHasFullTextIndex_Fires()
    {
        var findings = ScanSwitchFullTextIndexes(sourceHasFullTextIndex: false, targetHasFullTextIndex: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction);
        Assert.Contains("4918", finding.DetailText);
        Assert.Contains("dbo.SwTgt", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_NeitherHasFullTextIndex_NeverFires()
    {
        var findings = ScanSwitchFullTextIndexes(sourceHasFullTextIndex: false, targetHasFullTextIndex: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction);
    }

    private static DatabaseCatalog CatalogWithSwitchFilegroups(
        string? sourceFilegroup, bool sourceReadOnly, string? targetFilegroup, bool targetReadOnly)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { FilegroupName = sourceFilegroup, FilegroupIsReadOnly = sourceReadOnly });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { FilegroupName = targetFilegroup, FilegroupIsReadOnly = targetReadOnly });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchFilegroups(
        string? sourceFilegroup, bool sourceReadOnly, string? targetFilegroup, bool targetReadOnly)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchFilegroups(sourceFilegroup, sourceReadOnly, targetFilegroup, targetReadOnly));
    }

    [Fact]
    public void AlterTableSwitch_DifferentFilegroups_Fires()
    {
        var findings = ScanSwitchFilegroups("PRIMARY", sourceReadOnly: false, "FG_Orders", targetReadOnly: false);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4940", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_TargetInReadOnlyFilegroup_Fires()
    {
        var findings = ScanSwitchFilegroups("FG_Orders", sourceReadOnly: false, "FG_Orders", targetReadOnly: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch);
        Assert.Contains("4979", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SameReadWriteFilegroup_NeverFires()
    {
        var findings = ScanSwitchFilegroups("FG_Orders", sourceReadOnly: false, "FG_Orders", targetReadOnly: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch);
    }

    [Fact]
    public void AlterTableSwitch_PartitionedTableUnknownFilegroup_NeverFires()
    {

        var findings = ScanSwitchFilegroups(null, sourceReadOnly: false, "FG_Orders", targetReadOnly: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch);
    }

    private static DatabaseCatalog CatalogWithSwitchTemporal(bool sourceIsTemporal, bool targetIsTemporal)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        if (sourceIsTemporal)
        {
            catalog.AddTemporalTablePair(new TemporalTablePair("dbo.SwSrc", "dbo.SwSrcHistory"));
        }

        if (targetIsTemporal)
        {
            catalog.AddTemporalTablePair(new TemporalTablePair("dbo.SwTgt", "dbo.SwTgtHistory"));
        }

        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchTemporal(bool sourceIsTemporal, bool targetIsTemporal)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchTemporal(sourceIsTemporal, targetIsTemporal));
    }

    [Fact]
    public void AlterTableSwitch_TargetSystemVersionedSourceIsNot_Fires()
    {
        var findings = ScanSwitchTemporal(sourceIsTemporal: false, targetIsTemporal: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("13577", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceSystemVersionedTargetIsNot_Fires()
    {
        var findings = ScanSwitchTemporal(sourceIsTemporal: true, targetIsTemporal: false);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch);
        Assert.Contains("13577", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_BothSystemVersioned_NeverFires()
    {
        var findings = ScanSwitchTemporal(sourceIsTemporal: true, targetIsTemporal: true);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch);
    }

    [Fact]
    public void AlterTableSwitch_NeitherSystemVersioned_NeverFires()
    {
        var findings = ScanSwitchTemporal(sourceIsTemporal: false, targetIsTemporal: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch);
    }

    private static DatabaseCatalog CatalogWithSwitchRuleConstraint(bool sourceHasRule, bool targetHasRule)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { HasRuleConstraint = sourceHasRule });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { HasRuleConstraint = targetHasRule });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchRuleConstraint(bool sourceHasRule, bool targetHasRule)
    {
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchRuleConstraint(sourceHasRule, targetHasRule));
    }

    [Fact]
    public void AlterTableSwitch_TargetHasRuleConstraint_Fires()
    {
        var findings = ScanSwitchRuleConstraint(sourceHasRule: false, targetHasRule: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4964", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceHasRuleConstraint_Fires()
    {
        var findings = ScanSwitchRuleConstraint(sourceHasRule: true, targetHasRule: false);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint);
        Assert.Contains("4964", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_NeitherHasRuleConstraint_NeverFires()
    {
        var findings = ScanSwitchRuleConstraint(sourceHasRule: false, targetHasRule: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint);
    }

    private static DatabaseCatalog CatalogWithSwitchCdc(bool sourceDisallowed, bool targetDisallowed)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { CdcPartitionSwitchDisallowed = sourceDisallowed });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { CdcPartitionSwitchDisallowed = targetDisallowed });
        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchCdc(string switchSql, bool sourceDisallowed, bool targetDisallowed)
    {
        var result = SqlScriptParser.ParseText("test.sql", switchSql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchCdc(sourceDisallowed, targetDisallowed));
    }

    [Fact]
    public void AlterTableSwitch_TargetCdcPartitionSwitchDisallowed_Fires()
    {
        var findings = ScanSwitchCdc(
            "ALTER TABLE dbo.SwSrc SWITCH PARTITION 1 TO dbo.SwTgt PARTITION 1;",
            sourceDisallowed: false, targetDisallowed: true);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("22842", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SourceCdcPartitionSwitchDisallowed_Fires()
    {
        var findings = ScanSwitchCdc(
            "ALTER TABLE dbo.SwSrc SWITCH PARTITION 1 TO dbo.SwTgt PARTITION 1;",
            sourceDisallowed: true, targetDisallowed: false);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch);
        Assert.Contains("22843", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_NoPartitionNumber_CdcDisallowedNeverFires()
    {

        var findings = ScanSwitchCdc(
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;",
            sourceDisallowed: false, targetDisallowed: true);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch);
    }

    [Fact]
    public void AlterTableSwitch_CdcPartitionSwitchAllowed_NeverFires()
    {
        var findings = ScanSwitchCdc(
            "ALTER TABLE dbo.SwSrc SWITCH PARTITION 1 TO dbo.SwTgt PARTITION 1;",
            sourceDisallowed: false, targetDisallowed: false);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch);
    }

    private static DatabaseCatalog CatalogWithSwitchPartitionFilegroups(
        string? sourceScheme, string? targetScheme, IEnumerable<(string Scheme, int PartitionNumber, string Filegroup)> mappings)
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { PartitionSchemeName = sourceScheme });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { PartitionSchemeName = targetScheme });

        foreach (var (scheme, partitionNumber, filegroup) in mappings)
        {
            catalog.AddPartitionFilegroup(scheme, partitionNumber, filegroup);
        }

        return catalog;
    }

    private static IReadOnlyList<QueryAntiPatternFinding> ScanSwitchPartitionFilegroups(
        string switchSql, string? sourceScheme, string? targetScheme, IEnumerable<(string Scheme, int PartitionNumber, string Filegroup)> mappings)
    {
        var result = SqlScriptParser.ParseText("test.sql", switchSql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return QueryAntiPatternScanner.Scan(result, CatalogWithSwitchPartitionFilegroups(sourceScheme, targetScheme, mappings));
    }

    [Fact]
    public void AlterTableSwitch_DifferentSchemesSamePartitionNumberDifferentFilegroup_Fires()
    {
        var findings = ScanSwitchPartitionFilegroups(
            "ALTER TABLE dbo.SwSrc SWITCH PARTITION 1 TO dbo.SwTgt PARTITION 1;",
            sourceScheme: "PS_B", targetScheme: "PS_A",
            mappings: [("PS_A", 1, "FG_A"), ("PS_B", 1, "FG_B")]);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4938", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_NonPartitionedSourceDifferentFilegroupThanTargetPartition_Fires()
    {
        var ddl = "CREATE TABLE dbo.SwSrc (Id INT NOT NULL); CREATE TABLE dbo.SwTgt (Id INT NOT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", ddl);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        catalog.AddOrReplace(catalog.Find("dbo.SwSrc")! with { FilegroupName = "FG_B" });
        catalog.AddOrReplace(catalog.Find("dbo.SwTgt")! with { PartitionSchemeName = "PS_A" });
        catalog.AddPartitionFilegroup("PS_A", 1, "FG_A");

        var switchResult = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt PARTITION 1;");
        Assert.False(switchResult.HasErrors, string.Join("; ", switchResult.Errors.Select(e => e.Message)));
        var findings = QueryAntiPatternScanner.Scan(switchResult, catalog);

        var finding = Assert.Single(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch);
        Assert.Contains("4939", finding.DetailText);
    }

    [Fact]
    public void AlterTableSwitch_SamePartitionSchemeSamePartitionNumber_NeverFires()
    {
        var findings = ScanSwitchPartitionFilegroups(
            "ALTER TABLE dbo.SwSrc SWITCH PARTITION 1 TO dbo.SwTgt PARTITION 1;",
            sourceScheme: "PS_A", targetScheme: "PS_A",
            mappings: [("PS_A", 1, "FG_A")]);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch);
    }

    [Fact]
    public void AlterTableSwitch_BothNonPartitioned_PartitionFilegroupCheckNeverFires()
    {

        var findings = ScanSwitchPartitionFilegroups(
            "ALTER TABLE dbo.SwSrc SWITCH TO dbo.SwTgt;",
            sourceScheme: null, targetScheme: null, mappings: []);

        Assert.DoesNotContain(findings, f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch);
    }
}
