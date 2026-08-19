using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "String concatenation
/// via the + operator silently nulls the entire result when any operand is NULL" - see
/// <see cref="StringConcatNullFinding"/> for the full scope/precision story and oracle evidence.
/// </summary>
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
        // FirstName + ' ' + MiddleName + ' ' + LastName is one chain, one finding - not three.
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
    public void CteSharesNameWithRealTable_NeverFires()
    {
        // 2026-08 audit: DirectBaseTableResolver never consulted CTE scope at all - a CTE named
        // the same as dbo.Person silently resolved against the REAL table instead, matching
        // MiddleName against its real (nullable) column and firing on a statement that, through
        // the CTE, never reads the real table's MiddleName. A CTE is never schema-qualified, so
        // it always shadows a same-named real base table; DirectBaseTableResolver now declines a
        // CTE-shadowed reference entirely rather than mismatching it.
        var findings = Scan(
            "WITH Person AS (SELECT FirstName, MiddleName FROM dbo.Person WHERE MiddleName IS NOT NULL) " +
            "SELECT FirstName + ' ' + MiddleName FROM Person;");

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
        // Regression: COALESCE parses to its own dedicated ScriptDOM node type
        // (CoalesceExpression), never a generic FunctionCall the way ISNULL does - a real
        // false positive caught scanning the local test database's own corpus (a nested
        // LTRIM(RTRIM(COALESCE(chain1, chain2))) shape) before this was handled correctly.
        var findings = Scan(
            "SELECT LTRIM(RTRIM(COALESCE(FirstName + ' ' + MiddleName + ' ' + LastName, FirstName + ' ' + LastName))) FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NumericAddition_NeverFires()
    {
        // Both operands resolve to a non-string catalog category - this is arithmetic, not
        // concatenation, and out of this rule's scope entirely.
        var findings = Scan("SELECT Age + 1 FROM dbo.Person;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvableOperand_DeclinesRatherThanGuesses()
    {
        // @suffix is a variable, not a catalog column - this pass cannot prove its runtime type,
        // so the whole chain declines rather than assuming it's a harmless string.
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
    public void QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT p.FirstName + ' ' + p.MiddleName FROM dbo.Person p;");

        Assert.Single(findings);
    }

    [Fact]
    public void ThroughView_NotAnalyzed()
    {
        // Known v1 scope limit - only a direct base-table alias is resolved, never a view.
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
