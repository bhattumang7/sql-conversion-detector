using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class OnlineRebuildLegacyLobScannerTests
{
    private static IReadOnlyList<OnlineRebuildLegacyLobFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return OnlineRebuildLegacyLobScanner.Scan(catalog);
    }

    [Fact]
    public void AlterTableRebuild_Online_NTextColumn_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NTEXT NULL);
            ALTER TABLE dbo.Article REBUILD WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterTableRebuild, finding.Kind);
        Assert.Equal("dbo.Article", finding.TableQualifiedName);
        Assert.Equal("Body", finding.ColumnName);
    }

    [Fact]
    public void AlterTableRebuild_Offline_NTextColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NTEXT NULL);
            ALTER TABLE dbo.Article REBUILD;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterTableRebuild_Online_NoLegacyLobColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NULL);
            ALTER TABLE dbo.Article REBUILD WITH (ONLINE = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterIndexAllRebuild_Online_ImageColumn_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Attachment (AttachmentId INT NOT NULL PRIMARY KEY, Content IMAGE NULL);
            ALTER INDEX ALL ON dbo.Attachment REBUILD WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterIndexAllRebuild, finding.Kind);
        Assert.Equal("dbo.Attachment", finding.TableQualifiedName);
        Assert.Equal("Content", finding.ColumnName);
    }

    [Fact]
    public void AlterIndexSingleNamedRebuild_Online_ImageColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Attachment (AttachmentId INT NOT NULL PRIMARY KEY, Content IMAGE NULL);
            CREATE INDEX IX_Attachment ON dbo.Attachment (AttachmentId);
            ALTER INDEX IX_Attachment ON dbo.Attachment REBUILD WITH (ONLINE = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterIndexAllRebuild_Offline_ImageColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Attachment (AttachmentId INT NOT NULL PRIMARY KEY, Content IMAGE NULL);
            ALTER INDEX ALL ON dbo.Attachment REBUILD;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AlterTableRebuild_Online_TextColumn_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Legacy (LegacyId INT NOT NULL PRIMARY KEY, Notes TEXT NULL);
            ALTER TABLE dbo.Legacy REBUILD WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Notes", finding.ColumnName);
    }
}
