using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "UPDATE ... FROM without source uniqueness" - Structural/
/// AST tests for the extraction logic; the general nondeterminism mechanism and the MERGE-raises-
/// an-error contrast are oracle-confirmed separately via real execution in
/// <see cref="NonUniqueUpdateSourceOracleTests"/>.
/// </summary>
public sealed class NonUniqueUpdateSourceScannerTests
{
    private static IReadOnlyList<NonUniqueUpdateSourceFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.TargetT (Id INT NOT NULL PRIMARY KEY, Val INT NULL);" +
            "CREATE TABLE dbo.SourceNonUnique (TargetId INT NOT NULL, Val INT NOT NULL);" +
            "CREATE TABLE dbo.SourceUnique (TargetId INT NOT NULL UNIQUE, Val INT NOT NULL);" +
            "CREATE TABLE dbo.SourceCompositeUnique (TargetId INT NOT NULL, Cat INT NOT NULL, Val INT NOT NULL, CONSTRAINT UX_Composite UNIQUE (TargetId, Cat));" +
            extraDdl;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return NonUniqueUpdateSourceScanner.Scan(result, catalog);
    }

    [Fact]
    public void NonUniqueSource_SetClauseReadsFromIt_Fires()
    {
        var findings = Scan("UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.TargetT", finding.TargetTableQualifiedName);
        Assert.Equal("dbo.SourceNonUnique", finding.SourceTableQualifiedName);
        Assert.Equal(["TargetId"], finding.JoinColumnNames);
        Assert.Equal(["Val"], finding.SetColumnNames);
    }

    [Fact]
    public void UniqueIndexOnJoinColumn_NeverFires()
    {
        var findings = Scan("UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceUnique s ON t.Id = s.TargetId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void WhereClauseUnsatisfiable_NeverFires()
    {
        var findings = Scan(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId WHERE t.Id = 1 AND t.Id = 2;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateThroughCteNamedLikeTheRealTargetTable_NeverFires()
    {
        // 2026-08 audit: cteRelations was always null when resolving the UPDATE's own target
        // alias, so an updatable CTE named the same as dbo.TargetT (valid T-SQL: WITH TargetT AS
        // (...) UPDATE t SET ... is a real write-through-CTE pattern) silently resolved against
        // the REAL dbo.TargetT instead - misattributing a finding derived from the real table's
        // own identity to a statement whose actual write target is a CTE, out of this scanner's
        // declared scope. A CTE is never schema-qualified, so it always shadows a same-named real
        // base table; resolved correctly, the target has no QualifiedName at all (a CTE relation
        // carries none), so this scanner must decline the whole statement.
        var findings = Scan(
            "WITH TargetT AS (SELECT Id, Val FROM dbo.TargetT) " +
            "UPDATE t SET t.Val = s.Val FROM TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExactCompositeUniqueMatch_NeverFires()
    {
        var findings = Scan(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceCompositeUnique s ON t.Id = s.TargetId AND t.Id = s.Cat;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CompositeUniqueSuperset_JoinOnSubsetOnly_StillFires()
    {
        // UNIQUE(TargetId, Cat) does NOT make a join on TargetId alone safe - the
        // precision-critical case this rule must not mis-suppress.
        var findings = Scan(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceCompositeUnique s ON t.Id = s.TargetId;");

        Assert.Single(findings);
    }

    [Fact]
    public void SubsetOfCompositeJoin_UniqueOnSubsetAlone_NeverFires()
    {
        // Joining on (TargetId, Region) where a unique index exists on TargetId alone is safe -
        // uniqueness on a subset implies uniqueness on any superset of the joined key.
        var findings = Scan(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceUnique s ON t.Id = s.TargetId AND t.Val = s.Val;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NonUniqueSource_SetClauseNeverReadsFromIt_NeverFires()
    {
        var findings = Scan("UPDATE t SET t.Val = 1 FROM dbo.TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoFromClause_SimpleUpdate_NeverFires()
    {
        var findings = Scan("UPDATE dbo.TargetT SET Val = 1 WHERE Id = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelfJoin_NonUniqueSourceSide_Fires()
    {
        // t2.Id is the table's own PK, which would be provably unique - join on t2.Val (not
        // unique) instead, to actually exercise the self-join firing case.
        var findings = Scan(
            "UPDATE t1 SET t1.Val = t2.Val FROM dbo.TargetT t1 JOIN dbo.TargetT t2 ON t1.Id = t2.Val;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.TargetT", finding.TargetTableQualifiedName);
        Assert.Equal("dbo.TargetT", finding.SourceTableQualifiedName);
    }

    [Fact]
    public void SetValueIsExpressionReferencingSource_StillFires()
    {
        var findings = Scan("UPDATE t SET t.Val = s.Val + 1 FROM dbo.TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId;");

        Assert.Single(findings);
    }

    [Fact]
    public void FilteredUniqueIndex_TreatedAsNotProvablyUnique_Fires()
    {
        var findings = Scan(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceFiltered s ON t.Id = s.TargetId;",
            extraDdl: "CREATE TABLE dbo.SourceFiltered (TargetId INT NOT NULL, Val INT NOT NULL); " +
                      "CREATE UNIQUE INDEX UX_Filtered ON dbo.SourceFiltered(TargetId) WHERE Val > 0;");

        Assert.Single(findings);
    }

    [Fact]
    public void IndirectJoinNotTouchingTarget_NeverFires()
    {
        // A joined to Target directly (safe, unique), but the SET clause reads from B - a table
        // two hops from the target, not directly joined to it - which is out of v1 scope.
        var findings = Scan(
            "UPDATE t SET t.Val = b.Val FROM dbo.TargetT t JOIN dbo.SourceUnique a ON t.Id = a.TargetId JOIN dbo.SourceNonUnique b ON a.TargetId = b.TargetId;",
            extraDdl: "");

        Assert.Empty(findings);
    }
}
