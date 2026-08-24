using System.Text.Json;
using SilentScan.Core.TypeInference;
using SilentScan.Verify;
using SilentScan.Verify.Commands;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class GenerateTypeMatrixCommandTests : IDisposable
{
    private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"silentscan-type-matrix-test-{Guid.NewGuid():N}.json");
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    public void Dispose()
    {
        if (File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }
    }

    [Fact]
    public async Task RunAsync_SmallFamilySubset_WritesEnvelopeWithOnlyRequestedPairsAndReportsCountOnStdout()
    {
        var numericSubset = new (SqlTypeCategory Category, string Syntax)[]
        {
            (SqlTypeCategory.Int, "INT"),
            (SqlTypeCategory.Real, "REAL"),
        };
        var stdout = new StringWriter();

        var exitCode = await GenerateTypeMatrixCommand.RunAsync(
            _outputPath,
            Options,
            stdout,
            numericFamily: numericSubset,
            dateTimeFamily: [],
            stringFamily: [],
            collations: [],
            crossFamilyOther: [],
            binaryFamily: []);

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_outputPath));
        var root = document.RootElement;

        var entries = root.GetProperty("Entries");
        Assert.Equal(2, entries.GetArrayLength());

        var intVsReal = entries.EnumerateArray().Single(e =>
            e.GetProperty("ColumnCategory").GetString() == "Int" && e.GetProperty("OtherCategory").GetString() == "Real");
        Assert.Equal(JsonValueKind.String, intVsReal.GetProperty("ColumnCategory").ValueKind);
        Assert.True(intVsReal.GetProperty("ColumnConverts").GetBoolean());
        Assert.False(intVsReal.GetProperty("CompileFailed").GetBoolean());

        var realVsInt = entries.EnumerateArray().Single(e =>
            e.GetProperty("ColumnCategory").GetString() == "Real" && e.GetProperty("OtherCategory").GetString() == "Int");
        Assert.False(realVsInt.GetProperty("ColumnConverts").GetBoolean());

        var serverVersion = root.GetProperty("ServerVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(serverVersion));

        var probedAt = root.GetProperty("ProbedAtUtc").GetString();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", probedAt!);

        var notes = root.GetProperty("Notes").GetString();
        Assert.Contains("ColumnConverts=true means a CONVERT_IMPLICIT node targets the column", notes);
        Assert.Contains("DynamicRangeSeekAvailable=true means the plan contains an Intrinsic GetRangeThroughConvert node", notes);
        Assert.Contains("CompileFailed=true means SQL Server rejected the comparison outright", notes);

        var rawText = await File.ReadAllTextAsync(_outputPath);
        Assert.EndsWith(Environment.NewLine, rawText);

        var expectedStdout = $"Wrote 2 entries (server {serverVersion}) to {_outputPath}" + Environment.NewLine;
        Assert.Equal(expectedStdout, stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_EmptyFamilies_WritesEmptyEntriesArray()
    {
        var stdout = new StringWriter();

        var exitCode = await GenerateTypeMatrixCommand.RunAsync(
            _outputPath,
            Options,
            stdout,
            numericFamily: [],
            dateTimeFamily: [],
            stringFamily: [],
            collations: [],
            crossFamilyOther: [],
            binaryFamily: []);

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_outputPath));
        var entries = document.RootElement.GetProperty("Entries");
        Assert.Equal(0, entries.GetArrayLength());

        Assert.Equal($"Wrote 0 entries (server unknown) to {_outputPath}" + Environment.NewLine, stdout.ToString());
    }
}
