using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class ParseHealthReportBuilderTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    [Fact]
    public void Build_CleanFixture_ReportsNoErrorsAndFullSuccessRate()
    {
        var files = SqlFileDiscovery.EnumerateSqlFiles(Path.Combine(FixturesDir, "phase0_spike.sql"));

        var report = ParseHealthReportBuilder.Build(files);

        Assert.Equal(1, report.TotalFiles);
        Assert.Equal(0, report.FilesWithErrors);
        Assert.Equal(1.0, report.ParseSuccessRate);
    }

    [Fact]
    public void Build_MalformedSql_ReportsErrorsAndReducedSuccessRate()
    {
        var tempDir = Directory.CreateTempSubdirectory("silentscan-tests-");
        try
        {
            var badFile = Path.Combine(tempDir.FullName, "broken.sql");
            File.WriteAllText(badFile, "SELECT FROM WHERE;;;");

            var files = SqlFileDiscovery.EnumerateSqlFiles(tempDir.FullName);
            var report = ParseHealthReportBuilder.Build(files);

            Assert.Equal(1, report.TotalFiles);
            Assert.Equal(1, report.FilesWithErrors);
            Assert.Equal(0.0, report.ParseSuccessRate);
            Assert.NotEmpty(report.Files.Single().Errors);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_FileWithOneBadBatchAmongGoodOnes_ReportsBatchGranularity()
    {
        var tempDir = Directory.CreateTempSubdirectory("silentscan-tests-");
        try
        {
            var mixedFile = Path.Combine(tempDir.FullName, "mixed.sql");
            File.WriteAllText(
                mixedFile,
                """
                CREATE TABLE dbo.A (Id INT NOT NULL);
                GO
                CREATE TABLE dbo.B ((( BAD SYNTAX HERE;
                GO
                CREATE TABLE dbo.C (Id INT NOT NULL);
                GO
                """);

            var files = SqlFileDiscovery.EnumerateSqlFiles(tempDir.FullName);
            var report = ParseHealthReportBuilder.Build(files);

            var health = Assert.Single(report.Files);
            Assert.NotEmpty(health.Errors);
            Assert.Equal(2, health.BatchCount);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_EmptyFileList_ReportsFullSuccessRate()
    {
        var report = ParseHealthReportBuilder.Build([]);

        Assert.Equal(0, report.TotalFiles);
        Assert.Equal(1.0, report.ParseSuccessRate);
        Assert.True(report.PassesDialectSniffing);
    }

    [Fact]
    public void PassesDialectSniffing_RateAtOrAboveNinetyPercent_IsTrue()
    {

        var report = new ParseHealthReport([
            new FileParseHealth("a.sql", [], BatchCount: 1),
            new FileParseHealth("b.sql", [], BatchCount: 1),
            new FileParseHealth("c.sql", [], BatchCount: 1),
            new FileParseHealth("d.sql", [], BatchCount: 1),
            new FileParseHealth("e.sql", [], BatchCount: 1),
            new FileParseHealth("f.sql", [], BatchCount: 1),
            new FileParseHealth("g.sql", [], BatchCount: 1),
            new FileParseHealth("h.sql", [], BatchCount: 1),
            new FileParseHealth("i.sql", [], BatchCount: 1),
            new FileParseHealth("j.sql", [new ParseErrorInfo(1, 1, 102, "bad")], BatchCount: 0),
        ]);

        Assert.Equal(0.9, report.ParseSuccessRate);
        Assert.True(report.PassesDialectSniffing);
    }

    [Fact]
    public void PassesDialectSniffing_RateBelowNinetyPercent_IsFalse()
    {
        var report = new ParseHealthReport([
            new FileParseHealth("a.sql", [], BatchCount: 1),
            new FileParseHealth("b.sql", [], BatchCount: 1),
            new FileParseHealth("c.sql", [], BatchCount: 1),
            new FileParseHealth("d.sql", [], BatchCount: 1),
            new FileParseHealth("e.sql", [], BatchCount: 1),
            new FileParseHealth("f.sql", [], BatchCount: 1),
            new FileParseHealth("g.sql", [], BatchCount: 1),
            new FileParseHealth("h.sql", [], BatchCount: 1),
            new FileParseHealth("i.sql", [new ParseErrorInfo(1, 1, 102, "bad")], BatchCount: 0),
            new FileParseHealth("j.sql", [new ParseErrorInfo(1, 1, 102, "bad")], BatchCount: 0),
        ]);

        Assert.Equal(0.8, report.ParseSuccessRate);
        Assert.False(report.PassesDialectSniffing);
    }
}
