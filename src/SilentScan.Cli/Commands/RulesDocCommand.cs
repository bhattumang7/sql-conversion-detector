using System.CommandLine;
using SilentScan.Core.Reporting;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan rules-doc` - regenerates <c>docs/rules.html</c> (the index) and
/// <c>docs/rules/*.html</c> (one page per rule) from <see cref="RuleCatalog"/>, the same single
/// source of truth SARIF's own <c>rules</c> block reads. Never hand-edit either; run this
/// instead. Reads fixture paths relative to the current directory, so run it from the repo root.
/// </summary>
public static class RulesDocCommand
{
    public static Command Create()
    {
        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the generated rules index page to.",
            DefaultValueFactory = _ => "docs/rules.html",
        };

        var rulesDirOption = new Option<string>("--rules-dir")
        {
            Description = "Directory to write one page per rule into.",
            DefaultValueFactory = _ => "docs/rules",
        };

        var command = new Command("rules-doc", "Regenerate docs/rules.html and docs/rules/*.html from RuleCatalog.")
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
