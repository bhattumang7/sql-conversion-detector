using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Parsing;

/// <summary>
/// Thin wrapper around ScriptDOM's TSql160Parser (SQL Server 2022 / compat level 160,
/// matching the pinned Verify/Bench environment). Tolerates and surfaces parse errors
/// rather than throwing, since corpus scanning must survive individual bad files.
/// </summary>
public static class SqlScriptParser
{
    public static SqlParseResult ParseText(string sourcePath, string sql) =>
        Parse(sourcePath, sql, initialQuotedIdentifiers: true);

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

        var best = Parse(path, text, initialQuotedIdentifiers: true);
        if (best.Errors.Count > 0)
        {
            best = Better(best, Parse(path, text, initialQuotedIdentifiers: false));
        }

        return best;
    }

    /// <summary>
    /// Reads and decodes <paramref name="path"/> using the same BOM-detection/Latin-1 fallback
    /// <see cref="ParseFile"/> uses internally, without parsing it - for callers that need to
    /// transform the text (e.g. corpus template token substitution) before handing it to <see
    /// cref="ParseText"/>. Exists because a plain <c>File.ReadAllText</c> silently mis-decodes a
    /// Windows-1252/Latin-1 corpus file as replacement-character-laden UTF-8 rather than failing
    /// visibly - the same failure mode <see cref="ParseFile"/> already guards against, which a
    /// caller reading bytes directly would otherwise bypass entirely.
    /// </summary>
    public static string DecodeFile(string path) => DecodeText(File.ReadAllBytes(path));

    private static SqlParseResult Parse(string sourcePath, string sql, bool initialQuotedIdentifiers)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        return new SqlParseResult(sourcePath, fragment, errors.ToList());
    }

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
