using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Aggregate argument
/// containing a division ... on a table with a columnstore or batch-mode-eligible index" - see
/// <see cref="AggregateDivisionColumnstoreFinding"/> for the full scope/precision story, including
/// the honest live-reproduction attempt.
/// </summary>
public sealed class AggregateDivisionColumnstoreScannerTests
{
    private static IReadOnlyList<AggregateDivisionColumnstoreFinding> Scan(string sql, bool withColumnstoreIndex = true)
    {
        var indexDdl = withColumnstoreIndex
            ? "\nGO\nCREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Ratios ON dbo.Ratios (Id, Num, Denom);"
            : string.Empty;
        var ddl = "CREATE TABLE dbo.Ratios (Id INT NOT NULL PRIMARY KEY, Num INT NOT NULL, Denom INT NOT NULL, Grp INT NOT NULL);" + indexDdl;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AggregateDivisionColumnstoreScanner.Scan(result, catalog);
    }

    [Fact]
    public void CaseGuardedDivisionInSum_OnColumnstoreTable_Fires()
    {
        var findings = Scan("SELECT SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.Ratios;");

        var finding = Assert.Single(findings);
        Assert.Equal("SUM", finding.AggregateFunctionName);
        Assert.Equal("dbo.Ratios", finding.TableQualifiedName);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void CaseGuardedDivisionInAvg_OnColumnstoreTable_Fires()
    {
        var findings = Scan("SELECT AVG(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.Ratios;");

        Assert.Single(findings);
    }

    [Fact]
    public void CaseGuardedDivisionInHaving_OnColumnstoreTable_Fires()
    {
        var findings = Scan(
            "SELECT Grp FROM dbo.Ratios GROUP BY Grp HAVING SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) > 10;");

        Assert.Single(findings);
    }

    [Fact]
    public void SimpleCaseGuardedDivision_OnColumnstoreTable_Fires()
    {
        var findings = Scan("SELECT SUM(CASE Denom WHEN 0 THEN 0 ELSE Num / Denom END) FROM dbo.Ratios;");

        Assert.Single(findings);
    }

    [Fact]
    public void CaseGuardedDivision_OnRowstoreTable_NoColumnstoreIndex_NeverFires()
    {
        // No columnstore index present - the structural precondition this rule needs isn't met.
        var findings = Scan(
            "SELECT SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.Ratios;",
            withColumnstoreIndex: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void DivisionByLiteralConstant_NeverFires()
    {
        // A literal divisor can never be zero - not error-prone regardless of execution mode.
        var findings = Scan("SELECT SUM(CASE WHEN Denom <> 0 THEN Num / 100 ELSE 0 END) FROM dbo.Ratios;");

        Assert.Empty(findings);
    }

    [Fact]
    public void DivisionOutsideAnyAggregate_NeverFires()
    {
        var findings = Scan("SELECT CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END FROM dbo.Ratios;");

        Assert.Empty(findings);
    }

    [Fact]
    public void AggregateWithNoCaseAtAll_NeverFires()
    {
        var findings = Scan("SELECT SUM(Num) FROM dbo.Ratios;");

        Assert.Empty(findings);
    }

    [Fact]
    public void AggregateOfCaseWithNoDivision_NeverFires()
    {
        var findings = Scan("SELECT SUM(CASE WHEN Denom <> 0 THEN Num ELSE 0 END) FROM dbo.Ratios;");

        Assert.Empty(findings);
    }
}
