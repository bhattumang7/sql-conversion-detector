using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Halloween Protection and self-referencing DML" - structural/
/// AST tests for the extraction logic; the underlying "extra defensive plan work appears" claim
/// (and its correction from the checklist's own "always an eager spool" premise to "a spool OR a
/// sort, depending on statement shape") is oracle-confirmed separately in
/// <see cref="SelfReferencingDmlOracleTests"/>.
/// </summary>
public sealed class SelfReferencingDmlScannerTests
{
    private const string Ddl = """
        CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Val INT NOT NULL, Flag BIT NOT NULL);
        CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, RefId INT NOT NULL);
        GO
        CREATE VIEW dbo.vT AS SELECT Id, Val, Flag FROM dbo.T;
        GO
        CREATE VIEW dbo.vOther AS SELECT Id, RefId FROM dbo.Other;
        """;

    private static IReadOnlyList<SelfReferencingDmlFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{Ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
        var viewExpansionMap = ViewExpansionMap.Build(views, catalog);
        return SelfReferencingDmlScanner.Scan(result, catalog, viewExpansionMap);
    }

    [Fact]
    public void InsertHoleFillingWithNotExists_Fires()
    {
        var findings = Scan("INSERT INTO dbo.T (Id, Val, Flag) SELECT Id + 1000, Val, 0 FROM dbo.T t WHERE NOT EXISTS (SELECT 1 FROM dbo.T t2 WHERE t2.Id = t.Id + 1000);");

        var finding = Assert.Single(findings);
        Assert.Equal(SelfReferencingDmlFindingKind.DirectTableReference, finding.Kind);
        Assert.Equal("INSERT", finding.StatementKind);
        Assert.Equal("dbo.T", finding.TargetTableQualifiedName);
        Assert.Equal("dbo.T", finding.ReadSideQualifiedName);
    }

    [Fact]
    public void InsertFromDifferentTable_NeverFires()
    {
        var findings = Scan("INSERT INTO dbo.T (Id, Val, Flag) SELECT Id + 5000, RefId, 0 FROM dbo.Other;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateFromSelfJoin_Fires()
    {
        var findings = Scan("UPDATE t1 SET t1.Val = t2.Val FROM dbo.T t1 JOIN dbo.T t2 ON t1.Id = t2.Id - 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(SelfReferencingDmlFindingKind.DirectTableReference, finding.Kind);
        Assert.Equal("UPDATE", finding.StatementKind);
        Assert.Equal("dbo.T", finding.TargetTableQualifiedName);
    }

    [Fact]
    public void UpdateFromJoinToDifferentTable_NeverFires()
    {
        var findings = Scan("UPDATE t1 SET t1.Val = o.RefId FROM dbo.T t1 JOIN dbo.Other o ON t1.Id = o.Id;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateNoFromClauseNoSelfReference_NeverFires()
    {
        var findings = Scan("UPDATE dbo.T SET Val = Val + 1 WHERE Flag = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateWhereExistsSelfSubquery_Fires()
    {
        var findings = Scan("UPDATE dbo.T SET Val = Val + 1 WHERE EXISTS (SELECT 1 FROM dbo.T t2 WHERE t2.Id = T.Id - 1);");

        var finding = Assert.Single(findings);
        Assert.Equal("UPDATE", finding.StatementKind);
        Assert.Equal("dbo.T", finding.TargetTableQualifiedName);
    }

    [Fact]
    public void UpdateSetClauseSelfSubquery_Fires()
    {
        var findings = Scan("UPDATE dbo.T SET Val = (SELECT MAX(Val) FROM dbo.T t2 WHERE t2.Id < T.Id) WHERE Flag = 1;");

        Assert.Single(findings);
    }

    [Fact]
    public void UpdateThroughCteNamedLikeARealTable_NeverResolvesTargetAgainstTheRealTable()
    {
        // 2026-08 audit: an updatable CTE (WITH T AS (...) UPDATE T SET ...) is valid T-SQL and
        // writes through to the CTE's own underlying base table - but a CTE is never schema-
        // qualified, so it always shadows dbo.T for this statement's own lifetime. Resolving
        // through the catalog instead (cteRelations always null, pre-fix) matched the write
        // target against the REAL dbo.T by coincidence of name, which is out of this scanner's
        // own declared scope (base-table write targets only) - the target must resolve to
        // nothing (a CTE relation carries no QualifiedName), so no finding should ever fire here.
        var findings = Scan("WITH T AS (SELECT Id, Val, Flag FROM dbo.T) UPDATE T SET Val = (SELECT MAX(Val) FROM dbo.T);");

        Assert.Empty(findings);
    }

    [Fact]
    public void DeleteWhereExistsSelf_Fires()
    {
        var findings = Scan("DELETE FROM dbo.T WHERE EXISTS (SELECT 1 FROM dbo.T t2 WHERE t2.Id = T.Id - 1);");

        var finding = Assert.Single(findings);
        Assert.Equal(SelfReferencingDmlFindingKind.DirectTableReference, finding.Kind);
        Assert.Equal("DELETE", finding.StatementKind);
    }

    [Fact]
    public void DeleteWhereExistsDifferentTable_NeverFires()
    {
        var findings = Scan("DELETE FROM dbo.T WHERE EXISTS (SELECT 1 FROM dbo.Other o WHERE o.Id = T.Id);");

        Assert.Empty(findings);
    }

    [Fact]
    public void MergeUsingSameTargetTable_Fires()
    {
        var findings = Scan(
            """
            MERGE dbo.T AS tgt
            USING (SELECT Id, Val FROM dbo.T) AS src
            ON tgt.Id = src.Id + 1
            WHEN MATCHED THEN UPDATE SET tgt.Val = src.Val
            WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Val, Flag) VALUES (src.Id + 1, src.Val, 0);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SelfReferencingDmlFindingKind.DirectTableReference, finding.Kind);
        Assert.Equal("MERGE", finding.StatementKind);
    }

    [Fact]
    public void MergeUsingDifferentSourceTable_NeverFires()
    {
        var findings = Scan(
            """
            MERGE dbo.T AS tgt
            USING (SELECT Id, RefId AS Val FROM dbo.Other) AS src
            ON tgt.Id = src.Id
            WHEN MATCHED THEN UPDATE SET tgt.Val = src.Val
            WHEN NOT MATCHED BY TARGET THEN INSERT (Id, Val, Flag) VALUES (src.Id, src.Val, 0);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void InsertThroughViewOverTargetTable_FiresAsThroughView()
    {
        var findings = Scan("INSERT INTO dbo.T (Id, Val, Flag) SELECT Id + 2000, Val, 0 FROM dbo.vT v WHERE NOT EXISTS (SELECT 1 FROM dbo.vT v2 WHERE v2.Id = v.Id + 2000);");

        var finding = Assert.Single(findings);
        Assert.Equal(SelfReferencingDmlFindingKind.ThroughView, finding.Kind);
        Assert.Equal("dbo.T", finding.TargetTableQualifiedName);
        Assert.Equal("dbo.vT", finding.ReadSideQualifiedName);
    }

    [Fact]
    public void InsertThroughViewOverDifferentTable_NeverFires()
    {
        var findings = Scan("INSERT INTO dbo.T (Id, Val, Flag) SELECT Id + 3000, RefId, 0 FROM dbo.vOther;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OneFindingPerStatement_NotOnePerMatchingReference()
    {
        // Two independent self-references in the same UPDATE (an extra JOIN plus a WHERE
        // subquery) still yields exactly one finding - the fact this rule reports is "does the
        // statement's own read side re-read the target," not an occurrence count.
        var findings = Scan(
            "UPDATE t1 SET t1.Val = t2.Val FROM dbo.T t1 JOIN dbo.T t2 ON t1.Id = t2.Id - 1 WHERE EXISTS (SELECT 1 FROM dbo.T t3 WHERE t3.Id = t1.Id - 2);");

        Assert.Single(findings);
    }

    [Fact]
    public void ReadSideReferenceIsACteSharingTheTargetsOwnBareName_NeverFires()
    {
        // The SET clause's subquery references "T" - inside this statement's own WITH clause,
        // that name is shadowed by the CTE (itself sourced from dbo.Other, never dbo.T), so the
        // subquery never actually re-reads the real target dbo.T at all. A CTE is never schema-
        // qualified, so an unqualified read-side reference whose bare name happens to match the
        // target's own base identifier must be checked against the statement's own CTE names
        // before being treated as a same-table match.
        var findings = Scan(
            """
            ;WITH T AS (SELECT Id FROM dbo.Other)
            UPDATE dbo.T SET Val = (SELECT COUNT(*) FROM T);
            """);

        Assert.Empty(findings);
    }
}
