using System.CommandLine;
using SilentScan.Core.Reporting;

namespace SilentScan.Cli.Commands;

public static class RulesDocCommand
{
    public static Command Create()
    {
        const string outputOptionDescription = "Path to write the generated rules index page to.";
        const string rulesDirOptionDescription = "Directory to write one page per rule into.";

        var outputOption = new Option<string>("--output")
        {
            Description = outputOptionDescription,
            DefaultValueFactory = _ => "docs/rules.html",
        };

        var rulesDirOption = new Option<string>("--rules-dir")
        {
            Description = rulesDirOptionDescription,
            DefaultValueFactory = _ => "docs/rules",
        };

        var description = "Regenerate docs/rules.html and docs/rules/*.html from RuleCatalog.\n\nOptions:\n"
            + $"  --output <output> (default: docs/rules.html) - {outputOptionDescription}\n"
            + $"  --rules-dir <rules-dir> (default: docs/rules) - {rulesDirOptionDescription}";

        var command = new Command("rules-doc", description)
        {
            outputOption,
            rulesDirOption,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var outputPath = parseResult.GetValue(outputOption)!;
            var rulesDir = parseResult.GetValue(rulesDirOption)!;
            var prunedCount = RulesDocGenerator.WriteAll(Directory.GetCurrentDirectory(), outputPath, rulesDir);
            Console.Error.WriteLine($"wrote {outputPath} and {RuleCatalog.BaseRules.Count} rule pages under {rulesDir} ({prunedCount} orphaned page(s) pruned)");
            return Task.FromResult(0);
        });

        return command;
    }
}
