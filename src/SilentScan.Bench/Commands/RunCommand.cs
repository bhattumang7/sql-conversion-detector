using System.CommandLine;
using SilentScan.Bench.Execution;
using SilentScan.Bench.Reporting;
using SilentScan.Bench.Scenarios;
using SilentScan.Verify;

namespace SilentScan.Bench.Commands;

public static class RunCommand
{
    private static readonly int[] DefaultRowCounts = [10_000, 1_000_000, 10_000_000];

    public static Command Create()
    {
        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the cost table CSV to.",
            DefaultValueFactory = _ => "silentscan-bench-results.csv",
        };

        var rowsOption = new Option<int[]>("--rows")
        {
            Description = "Row counts to benchmark (default: 10000 1000000 10000000).",
            DefaultValueFactory = _ => DefaultRowCounts,
            AllowMultipleArgumentsPerToken = true,
        };

        var databaseOption = new Option<string>("--database")
        {
            Description = "Disposable database name to deploy synthetic benchmark tables into.",
            DefaultValueFactory = _ => "SilentScanBench",
        };

        var command = new Command("run", "Run the full benchmark matrix and write the cost table CSV.")
        {
            outputOption,
            rowsOption,
            databaseOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.GetValue(outputOption)!;
            var rows = parseResult.GetValue(rowsOption)!;
            var database = parseResult.GetValue(databaseOption)!;
            return await RunAsync(output, rows, database, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(string outputPath, IReadOnlyList<int> rowCounts, string databaseName, CancellationToken cancellationToken)
    {
        IReadOnlyList<TypePairScenario> scenarios =
        [
            TypePairScenario.VarCharVsNVarChar("SQL_Latin1_General_CP1_CI_AS"),
            TypePairScenario.VarCharVsNVarChar("Latin1_General_CI_AS"),
            TypePairScenario.IntVsBigInt(),
        ];

        var runner = new BenchmarkRunner(SqlServerOptions.LocalDocker);
        var results = await runner.RunAsync(databaseName, scenarios, rowCounts, cancellationToken);

        await File.WriteAllTextAsync(outputPath, CsvReportWriter.Write(results), cancellationToken);
        Console.WriteLine($"Wrote {results.Count} rows to {outputPath}");

        return 0;
    }
}
