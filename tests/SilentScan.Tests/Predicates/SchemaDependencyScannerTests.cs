using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// The catalog-only half of the scalar-UDF stream (docs/detection-checklist.md Tier 1 #1) - a
/// computed column, DEFAULT, or CHECK constraint definition that calls a scalar UDF poisons
/// every query touching the table, detected from the catalog alone with no query-site AST.
/// </summary>
public sealed class SchemaDependencyScannerTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "scalar_udf");

    private static IReadOnlyList<ScalarUdfFinding> ScanSql(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return SchemaDependencyScanner.Scan(catalog);
    }

    private static IReadOnlyList<ScalarUdfFinding> ScanFixture(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        var result = SqlScriptParser.ParseFile(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return SchemaDependencyScanner.Scan(catalog);
    }

    [Fact]
    public void Fixture_ComputedColumn_RealCitedFunction_Fires()
    {
        var finding = Assert.Single(ScanFixture("COMPUTED_COLUMN_fires.sql"));
        Assert.Equal(SchemaDependencyKind.ComputedColumn, finding.SchemaDependencyKind);
        Assert.Equal("dbo.discount_price", finding.FunctionQualifiedName);
    }

    [Fact]
    public void Fixture_ComputedColumn_PlainArithmetic_NeverFires()
    {
        Assert.Empty(ScanFixture("COMPUTED_COLUMN_clean.sql"));
    }

    [Fact]
    public void Fixture_DefaultConstraint_RealCitedFunction_Fires()
    {
        var finding = Assert.Single(ScanFixture("DEFAULT_CONSTRAINT_fires.sql"));
        Assert.Equal(SchemaDependencyKind.DefaultConstraint, finding.SchemaDependencyKind);
        Assert.Equal("dbo.YearDiff", finding.FunctionQualifiedName);
    }

    [Fact]
    public void Fixture_DefaultConstraint_ConstantLiteral_NeverFires()
    {
        Assert.Empty(ScanFixture("DEFAULT_CONSTRAINT_clean.sql"));
    }

    [Fact]
    public void Fixture_CheckConstraint_RealCitedFunction_Fires()
    {
        var finding = Assert.Single(ScanFixture("CHECK_CONSTRAINT_fires.sql"));
        Assert.Equal(SchemaDependencyKind.CheckConstraint, finding.SchemaDependencyKind);
        Assert.Equal("Sales.SalesQuantity", finding.FunctionQualifiedName);
    }

    [Fact]
    public void Fixture_CheckConstraint_PlainComparison_NeverFires()
    {
        Assert.Empty(ScanFixture("CHECK_CONSTRAINT_clean.sql"));
    }

    [Fact]
    public void ComputedColumnCallingScalarUdf_Fires()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL, Computed AS dbo.fn_Compute(Id));
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.SchemaDependency, finding.Kind);
        Assert.Equal(SchemaDependencyKind.ComputedColumn, finding.SchemaDependencyKind);
        Assert.Equal("dbo.fn_Compute", finding.FunctionQualifiedName);
        Assert.Equal("dbo.T", finding.ReferencedObjectQualifiedName);
    }

    [Fact]
    public void ComputedColumnWithNoUdfCall_DoesNotFire()
    {
        var findings = ScanSql("CREATE TABLE dbo.T (Id INT NOT NULL, Doubled AS Id * 2);");

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnDefaultCallingScalarUdf_Fires()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Default() RETURNS INT AS BEGIN RETURN 0; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL, Code INT NOT NULL DEFAULT (dbo.fn_Default()));
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaDependencyKind.DefaultConstraint, finding.SchemaDependencyKind);
        Assert.Equal("dbo.fn_Default", finding.FunctionQualifiedName);
    }

    [Fact]
    public void ColumnDefaultWithConstantLiteral_DoesNotFire()
    {
        var findings = ScanSql("CREATE TABLE dbo.T (Id INT NOT NULL, Code INT NOT NULL DEFAULT (0));");

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnLevelCheckConstraintCallingScalarUdf_Fires()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_IsValid(@x INT) RETURNS BIT AS BEGIN RETURN 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL CHECK (dbo.fn_IsValid(Id) = 1));
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaDependencyKind.CheckConstraint, finding.SchemaDependencyKind);
        Assert.Equal("dbo.fn_IsValid", finding.FunctionQualifiedName);
    }

    [Fact]
    public void TableLevelCheckConstraintCallingScalarUdf_Fires()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_IsValid(@x INT, @y INT) RETURNS BIT AS BEGIN RETURN 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL, Other INT NOT NULL, CONSTRAINT CK_T CHECK (dbo.fn_IsValid(Id, Other) = 1));
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaDependencyKind.CheckConstraint, finding.SchemaDependencyKind);
    }

    [Fact]
    public void CheckConstraintWithNoUdfCall_DoesNotFire()
    {
        var findings = ScanSql("CREATE TABLE dbo.T (Id INT NOT NULL CHECK (Id > 0));");

        Assert.Empty(findings);
    }

    [Fact]
    public void TempTableComputedColumn_NeverCaptured()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE #T (Id INT NOT NULL, Computed AS dbo.fn_Compute(Id));
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterTableAddComputedColumnCallingScalarUdf_Fires()
    {
        var findings = ScanSql("""
            CREATE FUNCTION dbo.fn_Compute(@x INT) RETURNS INT AS BEGIN RETURN @x + 1; END;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            ALTER TABLE dbo.T ADD Computed AS dbo.fn_Compute(Id);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaDependencyKind.ComputedColumn, finding.SchemaDependencyKind);
    }
}
