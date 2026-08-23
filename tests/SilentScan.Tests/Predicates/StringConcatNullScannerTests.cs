using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class StringConcatNullScannerTests
{
    private static IReadOnlyList<StringConcatNullFinding> Scan(string sql)
    {
        var ddl = """
            CREATE TABLE dbo.Person (
                Id INT NOT NULL PRIMARY KEY,
                FirstName VARCHAR(50) NOT NULL,
                MiddleName VARCHAR(50) NULL,
                LastName VARCHAR(50) NOT NULL,
                Age INT NULL
            );
            CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, Label VARCHAR(50) NULL);
            """;
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return StringConcatNullScanner.Scan(result, catalog);
    }

    [Fact]
    public void NullableColumnConcatenatedWithLiteral_Fires()
    {
        var findings = Scan("SELECT FirstName + ' ' + MiddleName FROM dbo.Person;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Person", finding.TableQualifiedName);
        Assert.Equal("MiddleName", finding.ColumnName);
    }

    [Fact]
    public void ThreeLeafChain_ReportsOnceForWholeChain()
    {
        var findings = Scan("SELECT FirstName + ' ' + MiddleName + ' ' + LastName FROM dbo.Person;");

        Assert.Single(findings);
    }

    [Fact]
    public void NonNullableColumnsOnly_NeverFires()
    {
        var findings = Scan("SELECT FirstName + ' ' + LastName FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CteSharesNameWithRealTable_ResolvesThroughCteToRealColumn_Fires()
    {
        var findings = Scan(
            "WITH Person AS (SELECT FirstName, MiddleName FROM dbo.Person WHERE MiddleName IS NOT NULL) " +
            "SELECT FirstName + ' ' + MiddleName FROM Person;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Person", finding.TableQualifiedName);
        Assert.Equal("MiddleName", finding.ColumnName);
    }

    [Fact]
    public void CteSharesNameWithRealTable_ButUnderlyingColumnNotNullable_NeverFires()
    {
        var findings = Scan(
            "WITH Person AS (SELECT FirstName, LastName FROM dbo.Person) " +
            "SELECT FirstName + ' ' + LastName FROM Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NullableOperandGuardedByIsNull_NeverFires()
    {
        var findings = Scan("SELECT FirstName + ' ' + ISNULL(MiddleName, '') FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NullableOperandGuardedByCoalesce_NeverFires()
    {
        var findings = Scan("SELECT FirstName + ' ' + COALESCE(MiddleName, '') FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void WholeChainGuardedByOuterIsNull_NeverFires()
    {
        var findings = Scan("SELECT ISNULL(FirstName + ' ' + MiddleName, '') FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CoalesceExpression_NotAFunctionCall_StillGuardsBothChains()
    {
        var findings = Scan(
            "SELECT LTRIM(RTRIM(COALESCE(FirstName + ' ' + MiddleName + ' ' + LastName, FirstName + ' ' + LastName))) FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NumericAddition_NeverFires()
    {
        var findings = Scan("SELECT Age + 1 FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvableOperand_DeclinesRatherThanGuesses()
    {
        var findings = Scan("DECLARE @suffix VARCHAR(10) = 'x'; SELECT FirstName + @suffix + MiddleName FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateSetClause_NullableColumnConcatenated_Fires()
    {
        var findings = Scan("UPDATE dbo.Person SET FirstName = FirstName + ' ' + MiddleName;");

        var finding = Assert.Single(findings);
        Assert.Equal("MiddleName", finding.ColumnName);
    }

    [Fact]
    public void FromClauseCasingDiffersFromDdl_ReportsFromClauseCasing()
    {
        var findings = Scan("SELECT FirstName + ' ' + MiddleName FROM DBO.PERSON;");

        var finding = Assert.Single(findings);
        Assert.Equal("DBO.PERSON", finding.TableQualifiedName);
    }

    [Fact]
    public void QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT p.FirstName + ' ' + p.MiddleName FROM dbo.Person p;");

        Assert.Single(findings);
    }

    [Fact]
    public void ThroughView_NotAnalyzed()
    {
        var ddl = """
            CREATE TABLE dbo.Person (
                Id INT NOT NULL PRIMARY KEY,
                FirstName VARCHAR(50) NOT NULL,
                MiddleName VARCHAR(50) NULL
            );
            GO
            CREATE VIEW dbo.PersonView AS SELECT FirstName, MiddleName FROM dbo.Person;
            """;
        var result = SqlScriptParser.ParseText(
            "test.sql", $"{ddl}\nGO\nSELECT FirstName + ' ' + MiddleName FROM dbo.PersonView;");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);

        var findings = StringConcatNullScanner.Scan(result, catalog);

        Assert.Empty(findings);
    }
}
