using System.CommandLine;
using SilentScan.Cli.Commands;

var rootCommand = new RootCommand("silentscan — static analyzer for SQL Server / T-SQL. 234 rules across 11 families (conversions and silent write loss, sargability, lineage metrics, catalog and constraint state, plan shape, control flow and transactions, dynamic SQL, code quality and security, index design, query anti-patterns, triggers and cross-module correctness), backed by an engine-authoritative catalog, a lineage pass and a plan-XML oracle. Read-only, local, and machine-readable (JSON/SARIF).")
{
    ScanCorpusLiveCommand.Create(),
    ScanDbCommand.Create(),
    RulesDocCommand.Create(),
};

return await rootCommand.Parse(args).InvokeAsync();
