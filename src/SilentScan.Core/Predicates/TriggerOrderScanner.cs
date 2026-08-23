using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class TriggerOrderScanner
{
    public static IReadOnlyList<TriggerOrderFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<TriggerOrderFinding>();

        var eligible = catalog.TriggerEvents.Where(e => !e.IsInsteadOf && !e.IsDisabled);

        foreach (var group in eligible.GroupBy(e => (e.TableQualifiedName, e.EventTypeDescription)))
        {
            var events = group.ToList();
            if (events.Count < 2)
            {
                continue;
            }

            var unordered = events.Where(e => !e.IsFirst && !e.IsLast).ToList();
            if (unordered.Count < 2)
            {
                continue;
            }

            var first = events[0];
            findings.Add(new TriggerOrderFinding(
                group.Key.TableQualifiedName,
                group.Key.EventTypeDescription,
                [.. unordered.Select(e => e.TriggerQualifiedName).OrderBy(n => n, StringComparer.Ordinal)],
                first.SourcePath, first.SourceLine));
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.EventTypeDescription, StringComparer.Ordinal),
        ];
    }
}
