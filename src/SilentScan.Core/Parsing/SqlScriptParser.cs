using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Parsing;

public static class SqlScriptParser
{
    public static SqlParseResult ParseText(string sourcePath, string sql) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers: true, compatibilityLevel: null);

public static SqlParseResult ParseText(string sourcePath, string sql, bool initialQuotedIdentifiers) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers, compatibilityLevel: null);

public static SqlParseResult ParseText(string sourcePath, string sql, bool initialQuotedIdentifiers, int? compatibilityLevel) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers, compatibilityLevel);

public static SqlParseResult ParseFile(string path)
    {
        var text = DecodeFile(path);

        var best = Parse(path, text, initialQuotedIdentifiers: true, compatibilityLevel: null);
        if (best.Errors.Count > 0)
        {
            best = Better(best, Parse(path, text, initialQuotedIdentifiers: false, compatibilityLevel: null));
        }

        return best;
    }

public static string DecodeFile(string path) => DecodeText(File.ReadAllBytes(path));

    private static SqlParseResult Parse(string sourcePath, string sql, bool initialQuotedIdentifiers, int? compatibilityLevel)
    {
        var parser = CreateParser(compatibilityLevel, initialQuotedIdentifiers);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        var unanalyzedBatches = FindUnanalyzedBatches(sourcePath, sql, fragment);
        return new SqlParseResult(sourcePath, fragment, errors.ToList(), unanalyzedBatches);
    }

private static List<UnanalyzedBatch> FindUnanalyzedBatches(string sourcePath, string sql, TSqlFragment fragment)
    {
        if (fragment is not TSqlScript script)
        {
            return [];
        }

        var survivingStarts = script.Batches.Select(b => b.StartOffset).ToList();
        List<UnanalyzedBatch>? unanalyzed = null;

        foreach (var (start, length, text) in GoBatchSplitter.SplitWithSpans(sql))
        {
            var end = start + length;
            if (survivingStarts.Any(s => s >= start && s < end))
            {
                continue;
            }

            var (kind, name) = DroppedBatchObjectSniffer.Sniff(text);
            var startLine = CountLines(sql, start);
            (unanalyzed ??= []).Add(new UnanalyzedBatch(sourcePath, startLine, kind, name));
        }

        return unanalyzed ?? [];
    }

    private static int CountLines(string sql, int upToOffset)
    {
        var line = 1;
        for (var i = 0; i < upToOffset && i < sql.Length; i++)
        {
            if (sql[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

private static TSqlParser CreateParser(int? compatibilityLevel, bool initialQuotedIdentifiers) => compatibilityLevel switch
    {
        null => new TSql180Parser(initialQuotedIdentifiers),
        >= 180 => new TSql180Parser(initialQuotedIdentifiers),
        >= 170 => new TSql170Parser(initialQuotedIdentifiers),
        >= 160 => new TSql160Parser(initialQuotedIdentifiers),
        >= 150 => new TSql150Parser(initialQuotedIdentifiers),
        >= 140 => new TSql140Parser(initialQuotedIdentifiers),
        >= 130 => new TSql130Parser(initialQuotedIdentifiers),
        >= 120 => new TSql120Parser(initialQuotedIdentifiers),
        >= 110 => new TSql110Parser(initialQuotedIdentifiers),
        _ => new TSql100Parser(initialQuotedIdentifiers),
    };

    private static SqlParseResult Better(SqlParseResult current, SqlParseResult candidate) =>
        candidate.Errors.Count < current.Errors.Count ? candidate : current;

    private static string DecodeText(byte[] bytes)
    {
        foreach (var candidate in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            var preamble = candidate.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            {
                return candidate.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
            }
        }

        var utf8Text = Encoding.UTF8.GetString(bytes);
        return utf8Text.Contains('�') ? Encoding.Latin1.GetString(bytes) : utf8Text;
    }
}
