using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class SelectiveXmlIndexValueColumnScanner
{
    private const int MaxKeyLengthBytes = 900;

    public static IReadOnlyList<SelectiveXmlIndexValueColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var promotedPathTypes = new Dictionary<string, TypeInference.SqlType?>(catalog.IdentifierComparer);
        foreach (var path in catalog.SelectiveXmlIndexPromotedPaths)
        {
            promotedPathTypes[Key(path.TableQualifiedName, path.IndexName, path.PathName)] = path.Type;
        }

        var findings = new List<SelectiveXmlIndexValueColumnFinding>();

        foreach (var reference in catalog.SecondarySelectiveXmlIndexReferences)
        {
            if (!promotedPathTypes.TryGetValue(Key(reference.TableQualifiedName, reference.PrimaryIndexName, reference.PathName), out var type)
                || type is not { } resolvedType)
            {
                continue;
            }

            if (resolvedType.IsMax)
            {
                findings.Add(new SelectiveXmlIndexValueColumnFinding(
                    reference.TableQualifiedName, reference.SecondaryIndexName, reference.PrimaryIndexName, reference.PathName,
                    resolvedType.ToString(), reference.SourcePath, reference.Line, SelectiveXmlIndexValueColumnFindingKind.LargeObject));
                continue;
            }

            if (!resolvedType.IsStringFamily || resolvedType.Length is not { } length)
            {
                continue;
            }

            var byteLength = resolvedType.IsUnicodeString ? length * 2 : length;
            if (byteLength > MaxKeyLengthBytes)
            {
                findings.Add(new SelectiveXmlIndexValueColumnFinding(
                    reference.TableQualifiedName, reference.SecondaryIndexName, reference.PrimaryIndexName, reference.PathName,
                    resolvedType.ToString(), reference.SourcePath, reference.Line, SelectiveXmlIndexValueColumnFindingKind.TooWide));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.SecondaryIndexName, StringComparer.Ordinal),
        ];
    }

    private static string Key(string table, string index, string path) => $"{table}\u0001{index}\u0001{path}";
}
