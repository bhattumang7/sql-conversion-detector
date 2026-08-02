using System.CommandLine;
using SilentScan.Cli.Commands;

var rootCommand = new RootCommand("silentscan — static analyzer for index-killing implicit conversions in T-SQL.")
{
    ScanCommand.Create(),
    ScanCorpusCommand.Create(),
    ScanDbCommand.Create(),
};

return await rootCommand.Parse(args).InvokeAsync();
