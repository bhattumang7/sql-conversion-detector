using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Column carries a
/// DEFAULT constraint and is still nullable" - see <see cref="DefaultNullableConstraintFinding"/>
/// for the full precision story and oracle evidence. Mirrors <see
/// cref="CheckConstraintScannerTests"/>'s own shape: hand-built-catalog unit tests for the
/// scanner's own logic, plus an end-to-end live-oracle test proving the real deployment path.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DefaultNullableConstraintScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns) =>
        new(schema, name, CatalogTableKind.Table, columns, [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Column(string name, bool isNullable) =>
        new(name, new SqlType(SqlTypeCategory.VarChar), isNullable, IsIdentity: false, IsComputed: false, IsPersisted: false);

    [Fact]
    public void NullableColumnWithDefault_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Status", isNullable: true)]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Status", "('Active')", "dbo.Orders", 1));

        var findings = DefaultNullableConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("Status", finding.ColumnName);
        Assert.Equal("('Active')", finding.DefaultDefinitionText);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NotNullColumnWithDefault_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Status", isNullable: false)]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Status", "('Active')", "dbo.Orders", 1));

        Assert.Empty(DefaultNullableConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void NullableColumnWithNoDefault_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Notes", isNullable: true)]));

        Assert.Empty(DefaultNullableConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void UnknownColumnReferencedInSchemaExpression_IsSkippedNotGuessed()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", []));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Ghost", "('Active')", "dbo.Orders", 1));

        Assert.Empty(DefaultNullableConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void ComputedColumnSchemaExpression_NeverMisreadAsDefault()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Total", isNullable: true)]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.ComputedColumn, "dbo.Orders", "Total", "([Qty]*[Price])", "dbo.Orders", 1));

        Assert.Empty(DefaultNullableConstraintScanner.Scan(catalog));
    }

    /// <summary>
    /// End-to-end against the real standing Docker oracle (a fresh, disposable database, dropped
    /// unconditionally afterward): a NULL supplied explicitly bypasses the default entirely, and
    /// this is the shape actually flagged.
    /// </summary>
    [Fact]
    public async Task LiveDeployment_NullableColumnWithDefault_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.DefaultNullableTarget (Id INT NOT NULL PRIMARY KEY, Status VARCHAR(20) NULL CONSTRAINT DF_DefaultNullableTarget_Status DEFAULT ('Active'));");

        var finding = Assert.Single(report.DefaultNullableConstraintFindings);
        Assert.Equal("dbo.DefaultNullableTarget", finding.TableQualifiedName);
        Assert.Equal("Status", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_NotNullColumnWithDefault_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.DefaultNotNullTarget (Id INT NOT NULL PRIMARY KEY, Status VARCHAR(20) NOT NULL CONSTRAINT DF_DefaultNotNullTarget_Status DEFAULT ('Active'));");

        Assert.Empty(report.DefaultNullableConstraintFindings);
    }
}
