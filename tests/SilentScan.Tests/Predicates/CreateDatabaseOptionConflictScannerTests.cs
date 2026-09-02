using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class CreateDatabaseOptionConflictScannerTests
{
    private static IReadOnlyList<CreateDatabaseOptionConflictFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CreateDatabaseOptionConflictScanner.Scan(result);
    }

    [Fact]
    public void ContainmentPartialWithCatalogCollation_Fires()
    {
        var findings = Scan("CREATE DATABASE SomeDatabase CONTAINMENT = PARTIAL WITH CATALOG_COLLATION = DATABASE_DEFAULT;");

        var finding = Assert.Single(findings);
        Assert.Equal(CreateDatabaseOptionConflictKind.ContainmentPartialAndCatalogCollation, finding.Kind);
    }

    [Fact]
    public void ContainmentNoneWithCatalogCollation_DoesNotFire()
    {
        var findings = Scan("CREATE DATABASE SomeDatabase CONTAINMENT = NONE WITH CATALOG_COLLATION = DATABASE_DEFAULT;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CatalogCollationWithoutContainmentClause_DoesNotFire()
    {
        var findings = Scan("CREATE DATABASE SomeDatabase WITH CATALOG_COLLATION = DATABASE_DEFAULT;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ContainmentPartialAlone_DoesNotFire()
    {
        var findings = Scan("CREATE DATABASE SomeDatabase CONTAINMENT = PARTIAL;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainCreateDatabase_DoesNotFire()
    {
        var findings = Scan("CREATE DATABASE SomeDatabase;");

        Assert.Empty(findings);
    }
}
