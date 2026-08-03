using System.CommandLine;
using SilentScan.Cli.Commands;

var rootCommand = new RootCommand("silentscan — static analyzer for index-killing implicit conversions in T-SQL.")
{
    ScanCommand.Create(),
    ScanCorpusCommand.Create(),
    ScanCorpusLiveCommand.Create(),
    ScanDbCommand.Create(),
};

return await rootCommand.Parse(args).InvokeAsync();
