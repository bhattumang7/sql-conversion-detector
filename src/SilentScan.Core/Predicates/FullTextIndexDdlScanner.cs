using System.Globalization;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FullTextIndexDdlScanner
{
    private const int MaxIndexedColumns = 1024;

    private static readonly HashSet<SqlTypeCategory> SupportedColumnTypeCategories =
    [
        SqlTypeCategory.Char,
        SqlTypeCategory.VarChar,
        SqlTypeCategory.NChar,
        SqlTypeCategory.NVarChar,
        SqlTypeCategory.Text,
        SqlTypeCategory.NText,
        SqlTypeCategory.Xml,
        SqlTypeCategory.Image,
    ];

    public static IReadOnlyList<FullTextIndexDdlFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<FullTextIndexDdlFinding>();

        foreach (var fullTextIndex in catalog.FullTextIndexes)
        {
            var table = catalog.Find(fullTextIndex.TableQualifiedName);
            if (table is not { Kind: CatalogTableKind.Table })
            {
                continue;
            }

            if (fullTextIndex.Columns.Count > MaxIndexedColumns)
            {
                findings.Add(new FullTextIndexDdlFinding(
                    FullTextIndexDdlFindingKind.TooManyIndexedColumns, fullTextIndex.TableQualifiedName, ColumnName: null,
                    $"{fullTextIndex.Columns.Count} columns (max {MaxIndexedColumns})",
                    fullTextIndex.SourcePath, fullTextIndex.Line, fullTextIndex.Column));
            }

            foreach (var ftColumn in fullTextIndex.Columns)
            {
                var column = table.FindColumn(ftColumn.ColumnName, catalog.IdentifierComparer);
                if (column is null)
                {
                    continue;
                }

                ScanColumn(fullTextIndex, ftColumn, column, findings);
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private static void ScanColumn(
        CatalogFullTextIndex fullTextIndex, CatalogFullTextIndexColumn ftColumn, CatalogColumn column, List<FullTextIndexDdlFinding> findings)
    {
        if (column.Type is { } type && !IsSupportedColumnType(type))
        {
            findings.Add(new FullTextIndexDdlFinding(
                FullTextIndexDdlFindingKind.UnsupportedColumnType, fullTextIndex.TableQualifiedName, column.Name,
                type.ToString(), fullTextIndex.SourcePath, fullTextIndex.Line, fullTextIndex.Column));
        }

        if (column.IsComputed && !column.IsPersisted && (column.IsComputedNonDeterministic || column.IsComputedImprecise))
        {
            findings.Add(new FullTextIndexDdlFinding(
                FullTextIndexDdlFindingKind.NonDeterministicComputedColumn, fullTextIndex.TableQualifiedName, column.Name,
                "nondeterministic or imprecise nonpersisted computed column", fullTextIndex.SourcePath, fullTextIndex.Line, fullTextIndex.Column));
        }

        if (ftColumn.LanguageTermRaw is { } languageTermRaw
            && TryParseNumericLcid(languageTermRaw, out var lcid)
            && !FullTextLanguageCatalog.InstalledLcids.Contains(lcid))
        {
            findings.Add(new FullTextIndexDdlFinding(
                FullTextIndexDdlFindingKind.InvalidLanguageId, fullTextIndex.TableQualifiedName, column.Name,
                $"LANGUAGE {languageTermRaw}", fullTextIndex.SourcePath, fullTextIndex.Line, fullTextIndex.Column));
        }
    }

    private static bool IsSupportedColumnType(SqlType type) =>
        SupportedColumnTypeCategories.Contains(type.Category)
        || (type.Category == SqlTypeCategory.VarBinary && type.IsMax);

    private static bool TryParseNumericLcid(string raw, out int lcid)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lcid);
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out lcid);
    }
}
