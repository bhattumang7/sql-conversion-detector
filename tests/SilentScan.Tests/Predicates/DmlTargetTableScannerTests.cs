using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DmlTargetTableScannerTests
{
    private static IReadOnlySet<string> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return DmlTargetTableScanner.Scan([result], catalog);
    }

    [Fact]
    public void PlainUpdateTarget_Fires()
    {
        var targets = Scan("CREATE TABLE dbo.T (Id INT NOT NULL);\nGO\nUPDATE dbo.T SET Id = 1;");

        Assert.Contains("dbo.T", targets);
    }

    [Fact]
    public void CteSharingNameWithUnrelatedRealTable_NeverAttributedToTheRealTable()
    {
        var targets = Scan(
            """
            CREATE TABLE dbo.Cte (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            ;WITH Cte AS (SELECT Id FROM dbo.T)
            UPDATE Cte SET Id = 1;
            """);

        Assert.DoesNotContain("dbo.Cte", targets);
    }
}
