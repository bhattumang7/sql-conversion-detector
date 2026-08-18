using System.CommandLine;
using SilentScan.Cli.Commands;

var rootCommand = new RootCommand("silentscan — static analyzer for SQL Server query-level performance defects that only an engine-authoritative catalog, a lineage pass, or a plan-XML oracle can detect precisely: index-killing implicit conversions, MSTVF-as-fence references, and scalar UDF cost (predicate/projection/lineage-inherited/schema-dependency).")
{
    ScanCorpusLiveCommand.Create(),
    ScanDbCommand.Create(),
    RulesDocCommand.Create(),
};

return await rootCommand.Parse(args).InvokeAsync();
