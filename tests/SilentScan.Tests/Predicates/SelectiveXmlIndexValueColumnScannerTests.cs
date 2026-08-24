using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SelectiveXmlIndexValueColumnScannerTests
{
    private static IReadOnlyList<SelectiveXmlIndexValueColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return SelectiveXmlIndexValueColumnScanner.Scan(catalog);
    }

    [Fact]
    public void SecondaryIndexOverPathWiderThan900Bytes_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL VARCHAR(901));
            CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Note);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("SXI_Orders_Note", finding.SecondaryIndexName);
        Assert.Equal("SXI_Orders", finding.PrimaryIndexName);
        Assert.Equal("Note", finding.PathName);
        Assert.Equal(SelectiveXmlIndexValueColumnFindingKind.TooWide, finding.Kind);
    }

    [Fact]
    public void SecondaryIndexOverPathAtExactly900Bytes_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL VARCHAR(900));
            CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Note);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SecondaryIndexOverNvarcharPath_DoublesByteLengthPastBoundary()
    {
        var oversized = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL NVARCHAR(451));
            CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Note);
            """);
        Assert.Single(oversized);

        var atBoundary = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL NVARCHAR(450));
            CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Note);
            """);
        Assert.Empty(atBoundary);
    }

    [Fact]
    public void SecondaryIndexOverMaxTypedPath_FiresAsLargeObject()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL VARCHAR(MAX));
            CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Note);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SelectiveXmlIndexValueColumnFindingKind.LargeObject, finding.Kind);
    }

    [Fact]
    public void PrimarySelectiveXmlIndexAlone_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL VARCHAR(MAX));
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SecondaryIndexOverNumericPath_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Amount = '/Order/Amount' AS SQL BIGINT);
            CREATE XML INDEX SXI_Orders_Amount ON dbo.Orders(Payload)
            USING XML INDEX SXI_Orders FOR (Amount);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SecondaryIndexOverPathFromDifferentTable_DoesNotCrossMatch()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE TABLE dbo.Invoices (InvoiceId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
            FOR (Note = '/Order/Note' AS SQL VARCHAR(901));
            CREATE SELECTIVE XML INDEX SXI_Invoices ON dbo.Invoices(Payload)
            FOR (Note = '/Invoice/Note' AS SQL VARCHAR(200));
            CREATE XML INDEX SXI_Invoices_Note ON dbo.Invoices(Payload)
            USING XML INDEX SXI_Invoices FOR (Note);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleSecondaryIndexesAcrossTables_OrderedByTableThenSecondaryIndexName()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.B (Id INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_B ON dbo.B(Payload)
            FOR (Zeta = '/A/Zeta' AS SQL VARCHAR(901), Alpha = '/A/Alpha' AS SQL VARCHAR(901));
            CREATE XML INDEX SXI_B_Zeta ON dbo.B(Payload) USING XML INDEX SXI_B FOR (Zeta);
            CREATE XML INDEX SXI_B_Alpha ON dbo.B(Payload) USING XML INDEX SXI_B FOR (Alpha);
            CREATE TABLE dbo.A (Id INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);
            CREATE SELECTIVE XML INDEX SXI_A ON dbo.A(Payload)
            FOR (Note = '/A/Note' AS SQL VARCHAR(901));
            CREATE XML INDEX SXI_A_Note ON dbo.A(Payload) USING XML INDEX SXI_A FOR (Note);
            """);

        Assert.Equal(3, findings.Count);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.B"], findings.Select(f => f.TableQualifiedName));
        Assert.Equal(["SXI_A_Note", "SXI_B_Alpha", "SXI_B_Zeta"], findings.Select(f => f.SecondaryIndexName));
    }
}
