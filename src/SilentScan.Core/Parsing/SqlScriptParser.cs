using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Parsing;

/// <summary>
/// Thin wrapper around ScriptDOM's TSqlNNNParser family. Tolerates and surfaces parse errors
/// rather than throwing, since corpus scanning must survive individual bad files. Defaults to
/// the newest available dialect (currently 180 / SQL Server 2025) when no target compat level is
/// known - a superset dialect parses older syntax fine, and the goal for an unknown target is
/// maximum acceptance, not fidelity to a guessed version. A caller with a real target
/// (<see cref="Catalog.DatabaseCatalog.CompatibilityLevel"/>, live-read, never guessed) should
/// pass it via the <c>compatibilityLevel</c> overloads instead - parsing a 170+-only construct
/// (e.g. <c>DATE_BUCKET</c>, <c>GENERATE_SERIES</c>) under a lower compat level's own parser
/// class fails exactly the way it would against the real target, rather than silently accepting
/// syntax the target server itself would reject.
/// </summary>
public static class SqlScriptParser
{
    public static SqlParseResult ParseText(string sourcePath, string sql) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers: true, compatibilityLevel: null);

    /// <summary>
    /// Overload for callers that have ground truth for the module's own <c>QUOTED_IDENTIFIER</c>
    /// setting (live catalog reads it from <c>sys.sql_modules.uses_quoted_identifier</c>). A
    /// module created under QI OFF uses <c>"..."</c> as a string literal (the legacy
    /// <c>EXEC("...")</c> idiom); parsing it under QI ON turns those into unclosed quoted
    /// identifiers and drops the batch. The 2-arg overload above (see
    /// <see cref="ParseText(string, string)"/>) stays at QI ON for callers with no ground truth
    /// (file scan, dynamic SQL, live-query guard), keeping the existing contract.
    /// </summary>
    public static SqlParseResult ParseText(string sourcePath, string sql, bool initialQuotedIdentifiers) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers, compatibilityLevel: null);

    /// <summary>
    /// Overload for a caller that also has the target's real, live-read compat level (a live/
    /// corpus module reparse) - see this class's own doc comment for why matching the parser
    /// dialect to it matters. <paramref name="compatibilityLevel"/> null behaves exactly like the
    /// 3-arg overload (newest dialect, unknown target).
    /// </summary>
    public static SqlParseResult ParseText(string sourcePath, string sql, bool initialQuotedIdentifiers, int? compatibilityLevel) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers, compatibilityLevel);

    /// <summary>
    /// Real-world corpus files aren't always encoded, or written, the way this scanner assumes
    /// by default (docs/audit-remediation-plan.md Phase 4.4, audit finding B4). Two distinct
    /// recovery mechanisms are combined here, each verified against the real parser rather than
    /// assumed:
    /// <list type="bullet">
    /// <item>Encoding is resolved BEFORE parsing, not by retrying after a failure - a wrong
    /// single-byte encoding (Windows-1252/Latin-1 corpora are common in older T-SQL scripts)
    /// never actually produces a parse ERROR, since ScriptDOM's lexer happily accepts the
    /// resulting U+FFFD replacement characters inside identifiers/strings/comments. Left alone
    /// it would "succeed" with silently wrong table/column names instead, which is worse than a
    /// visible failure. Latin-1 never fails to decode (every byte maps 1:1 to a code point), so
    /// it is used whenever the UTF-8 decode wasn't valid UTF-8 in the first place.</item>
    /// <item>QUOTED_IDENTIFIER genuinely does change what parses (verified: a schema-qualified
    /// double-quoted identifier like <c>dbo."Foo"</c> only parses under QUOTED_IDENTIFIER ON;
    /// some legacy scripts assume OFF) - retried only on failure, keeping whichever attempt has
    /// fewer errors.</item>
    /// </list>
    /// </summary>
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

    /// <summary>
    /// Reads and decodes <paramref name="path"/> using the same BOM-detection/Latin-1 fallback
    /// <see cref="ParseFile"/> uses internally, without parsing it - for callers that need to
    /// transform the text (e.g. corpus template token substitution) before handing it to <see
    /// cref="ParseText(string, string)"/>. Exists because a plain <c>File.ReadAllText</c> silently mis-decodes a
    /// Windows-1252/Latin-1 corpus file as replacement-character-laden UTF-8 rather than failing
    /// visibly - the same failure mode <see cref="ParseFile"/> already guards against, which a
    /// caller reading bytes directly would otherwise bypass entirely.
    /// </summary>
    public static string DecodeFile(string path) => DecodeText(File.ReadAllBytes(path));

    private static SqlParseResult Parse(string sourcePath, string sql, bool initialQuotedIdentifiers, int? compatibilityLevel)
    {
        var parser = CreateParser(compatibilityLevel, initialQuotedIdentifiers);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        var unanalyzedBatches = FindUnanalyzedBatches(sourcePath, sql, fragment);
        return new SqlParseResult(sourcePath, fragment, errors.ToList(), unanalyzedBatches);
    }

    /// <summary>
    /// A GO-separated batch with a syntax error never becomes a <see cref="TSqlBatch"/> at all -
    /// ScriptDOM just omits it from <see cref="TSqlScript.Batches"/> - so the only way to know
    /// one went missing is to independently split the same raw text on GO
    /// (<see cref="GoBatchSplitter.SplitWithSpans"/>) and diff against the batches that did
    /// survive. A raw span with no surviving batch whose <c>StartOffset</c> falls within it was
    /// dropped; its best-effort object identity is read from its own raw text only (see
    /// <see cref="DroppedBatchObjectSniffer"/>), never guessed. Raw text never leaves this
    /// method - only the small derived result is kept.
    /// </summary>
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

    /// <summary>
    /// ScriptDOM ships one parser class per compat level (100 through 180, in steps of 10) and no
    /// dialect below 100 at all - a compat level this scan has never seen (a very old instance,
    /// or a value the mapping doesn't yet know about) floors to 100 rather than guessing a newer
    /// one, since a lower-numbered parser is always the more conservative (more likely to reject,
    /// never to silently accept a construct the real target wouldn't) choice. Null (no known
    /// target) uses the newest available dialect - see this class's own doc comment.
    /// </summary>
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
        // A byte-order mark is an explicit, unambiguous encoding declaration and always wins.
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
