using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class OperandComparabilityScannerTests
{
    private static IReadOnlyList<OperandComparabilityFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.Document (Id INT NOT NULL PRIMARY KEY, Payload XML NOT NULL, Template XML NOT NULL, Name VARCHAR(50) NOT NULL);"
            + "CREATE TABLE dbo.Article (Id INT NOT NULL PRIMARY KEY, Body TEXT NOT NULL, Notes NTEXT NOT NULL, Picture IMAGE NOT NULL, Title VARCHAR(50) NOT NULL);"
            + "CREATE TABLE dbo.Ticket (Id INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL, Template JSON NOT NULL, Name VARCHAR(50) NOT NULL);"
            + (extraDdl.Length > 0 ? $"\nGO\n{extraDdl}" : string.Empty);
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return OperandComparabilityScanner.Scan(result, catalog);
    }

    [Fact]
    public void EqualityAgainstTwoXmlColumns_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Payload = Template;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Document", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal(OperandComparabilityFindingKind.Xml, finding.Kind);
        Assert.Equal(OperandComparabilityContext.Comparison, finding.Context);
        Assert.Equal("=", finding.OperatorText);
    }

    [Fact]
    public void RangeComparisonAgainstXmlColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Payload > Template;");

        var finding = Assert.Single(findings);
        Assert.Equal(">", finding.OperatorText);
    }

    [Fact]
    public void InPredicateAgainstXmlColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Payload IN (Template);");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.In, finding.Context);
    }

    [Fact]
    public void BetweenAgainstXmlColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Payload BETWEEN Template AND Template;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.Between, finding.Context);
    }

    [Fact]
    public void NullIfAgainstXmlColumn_Fires()
    {
        var findings = Scan("SELECT NULLIF(Payload, Template) FROM dbo.Document;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.NullIf, finding.Context);
    }

    [Fact]
    public void OrderByXmlColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document ORDER BY Payload;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.OrderBy, finding.Context);
    }

    [Fact]
    public void GroupByXmlColumn_Fires()
    {
        var findings = Scan("SELECT Payload, COUNT(*) FROM dbo.Document GROUP BY Payload;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.GroupBy, finding.Context);
    }

    [Fact]
    public void SelectDistinctXmlColumn_Fires()
    {
        var findings = Scan("SELECT DISTINCT Payload FROM dbo.Document;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.Distinct, finding.Context);
    }

    [Fact]
    public void CaseAndCoalesceBranchesOverXmlColumn_NeverFire()
    {
        var findings = Scan(
            "SELECT CASE WHEN 1 = 1 THEN Payload ELSE Template END, COALESCE(Payload, Template) FROM dbo.Document;");

        Assert.Empty(findings);
    }

    [Fact]
    public void IsNullAgainstXmlColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Payload IS NULL;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstPlainColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Document WHERE Id = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstTextColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Article WHERE Body = 'x';");

        var finding = Assert.Single(findings);
        Assert.Equal("Body", finding.ColumnName);
        Assert.Equal(OperandComparabilityFindingKind.LegacyLargeObject, finding.Kind);
    }

    [Fact]
    public void EqualityAgainstNTextColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Article WHERE Notes = N'x';");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstImageColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Article WHERE Picture = 0x00;");

        Assert.Single(findings);
    }

    [Fact]
    public void OrderByTextColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Article ORDER BY Body;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.OrderBy, finding.Context);
    }

    [Fact]
    public void LikeAgainstTextColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Article WHERE Body LIKE '%x%';");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectDistinctTextColumn_Fires()
    {
        var findings = Scan("SELECT DISTINCT Body FROM dbo.Article;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityContext.Distinct, finding.Context);
    }

    [Fact]
    public void EqualityAgainstTwoJsonColumns_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Ticket WHERE Payload = Template;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Ticket", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.Comparison, finding.Context);
        Assert.Equal("=", finding.OperatorText);
    }

    [Fact]
    public void InPredicateAgainstJsonColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Ticket WHERE Payload IN (Template);");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.In, finding.Context);
    }

    [Fact]
    public void BetweenAgainstJsonColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Ticket WHERE Payload BETWEEN Template AND Template;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.Between, finding.Context);
    }

    [Fact]
    public void NullIfAgainstJsonColumn_Fires()
    {
        var findings = Scan("SELECT NULLIF(Payload, Template) FROM dbo.Ticket;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.NullIf, finding.Context);
    }

    [Fact]
    public void OrderByJsonColumn_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.Ticket ORDER BY Payload;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.OrderBy, finding.Context);
    }

    [Fact]
    public void GroupByJsonColumn_Fires()
    {
        var findings = Scan("SELECT Payload, COUNT(*) FROM dbo.Ticket GROUP BY Payload;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.GroupBy, finding.Context);
    }

    [Fact]
    public void SelectDistinctJsonColumn_Fires()
    {
        var findings = Scan("SELECT DISTINCT Payload FROM dbo.Ticket;");

        var finding = Assert.Single(findings);
        Assert.Equal(OperandComparabilityFindingKind.Json, finding.Kind);
        Assert.Equal(OperandComparabilityContext.Distinct, finding.Context);
    }

    [Fact]
    public void IsNullAgainstJsonColumn_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.Ticket WHERE Payload IS NULL;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstXmlColumn_InJoinOnClause_Fires()
    {
        var findings = Scan(
            "SELECT d1.Id FROM dbo.Document d1 JOIN dbo.Document d2 ON d1.Payload = d2.Payload WHERE d1.Id = d2.Id;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstXmlColumn_InHavingClause_Fires()
    {
        var findings = Scan("SELECT Payload, Template FROM dbo.Document GROUP BY Payload, Template HAVING Payload = Template;");

        Assert.Contains(findings, f => f.Context == OperandComparabilityContext.Comparison && f.OperatorText == "=");
    }

    [Fact]
    public void EqualityAgainstXmlColumn_InUpdateWhere_Fires()
    {
        var findings = Scan("UPDATE dbo.Document SET Name = 'x' WHERE Payload = Template;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstXmlColumn_InDeleteWhere_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Document WHERE Payload = Template;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstXmlColumn_InsideCte_ResolvesRealUnderlyingColumn()
    {
        var findings = Scan(
            "WITH C AS (SELECT Id, Payload FROM dbo.Document) SELECT Id FROM C WHERE Payload = Payload;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Document", finding.TableQualifiedName);
    }

    [Fact]
    public void EqualityAgainstXmlColumn_ThroughView_NotAnalyzed()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.DocumentView WHERE Payload = Template;",
            extraDdl: "CREATE VIEW dbo.DocumentView AS SELECT Id, Payload, Template FROM dbo.Document;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstOuterAliasXmlColumn_InsideCorrelatedExistsSubquery_Fires()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.Document d WHERE EXISTS ("
            + "SELECT 1 FROM dbo.Article a WHERE a.Id = d.Id AND d.Payload = d.Template);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Document", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Fact]
    public void PositionedUpdateWhereCurrentOfCursor_NullSearchCondition_NeverThrows()
    {
        var findings = Scan(
            "DECLARE cur CURSOR FOR SELECT Id FROM dbo.Document; "
            + "OPEN cur; FETCH NEXT FROM cur; "
            + "UPDATE dbo.Document SET Name = 'x' WHERE CURRENT OF cur; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Empty(findings);
    }
}
