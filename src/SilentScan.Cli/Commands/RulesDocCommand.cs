using System.CommandLine;
using SilentScan.Core.Reporting;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan rules-doc` - regenerates <c>docs/rules.html</c> (CLAUDE.md's standing-docs entry
/// for it) from <see cref="RuleCatalog"/>, the same single source of truth SARIF's own
/// <c>rules</c> block reads. Never hand-edit that file; run this instead.
/// </summary>
public static class RulesDocCommand
{
    public static Command Create()
    {
        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the generated rules page to.",
            DefaultValueFactory = _ => "docs/rules.html",
        };

        var command = new Command("rules-doc", "Regenerate docs/rules.html from RuleCatalog.")
        {
            outputOption,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var outputPath = parseResult.GetValue(outputOption)!;
            File.WriteAllText(outputPath, RuleCatalogHtmlWriter.Write());
            Console.Error.WriteLine($"wrote {outputPath} ({RuleCatalog.BaseRules.Count} rules)");
            return Task.FromResult(0);
        });

        return command;
    }
}
