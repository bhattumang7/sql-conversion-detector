using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// Classifies a column-vs-other comparison per CLAUDE.md's type rules: only column-side
/// conversion loses the seek; collation family determines SCAN_FORCED vs RANGE_SEEK for
/// string-family conversions; unresolved collation is UNKNOWN, never a guess.
/// </summary>
public static class VerdictClassifier
{
    /// <param name="columnType">The indexed side's resolved type, or null when unresolvable.</param>
    /// <param name="otherType">The other operand's resolved type, or null when unresolvable.</param>
    /// <param name="otherIsLiteral">
    /// Whether the other operand is a genuine source-text literal, rather than a real column, a
    /// parameter/variable, or a CAST/CONVERT/function result - only matters for the same-
    /// category collation-mismatch branch (<see cref="ClassifySameCategory"/>). A literal is
    /// always T-SQL's "coercible default" coercibility tier: it never conflicts, so a differing
    /// collation there compiles fine and forces CONVERT_IMPLICIT onto the column - oracle-
    /// confirmed ScanForced. A real column, and (per official T-SQL coercibility rules) a CAST/
    /// CONVERT result that inherits a column's collation with no COLLATE clause of its own, both
    /// carry "implicit" coercibility instead - comparing two differing "implicit" collations is
    /// a compile error (Msg 468), not a seek-loss verdict, and this pass has not oracle-verified
    /// what a parameter/variable's own coercibility tier does here, so anything not provably a
    /// literal stays Unknown rather than guessed. Defaults to false (the conservative case).
    /// </param>
    public static Verdict Classify(SqlType? columnType, SqlType? otherType, bool otherIsLiteral = false)
    {
        if (columnType is null || otherType is null)
        {
            return Verdict.Unknown;
        }

        // sql_variant / xml / CLR user-defined types do not participate in the standard
        // conversion/precedence machinery (xml is not even comparable with '='; sql_variant
        // uses its own base-type hierarchy at compare time; text/ntext/image are legacy LOB
        // types with their own comparison quirks). Reporting a confident verdict here would
        // be a guess - CLAUDE.md's hard-cases list calls these out explicitly. Checked BEFORE
        // the same-category branch below: two xml columns being the "same category" does not
        // make `xml = xml` a real, seek-preserving comparison - it isn't comparable with '='
        // at all, and reporting SeekPreserved for it would be actively wrong, not just a
        // missed classification.
        if (IsOutOfModelCategory(columnType.Category) || IsOutOfModelCategory(otherType.Category))
        {
            return Verdict.Unknown;
        }

        if (columnType.Category == otherType.Category)
        {
            return ClassifySameCategory(columnType, otherType, otherIsLiteral);
        }

        // Every other cross-category pair - same family (int vs bigint, char vs nvarchar,
        // date vs datetime, ...) or cross-family (varchar column vs int/datetime/guid value,
        // and the reverse) - is decided by ONE authority: the Docker-oracle-probed matrix.
        // The official precedence list is not reliable enough on its own to report a verdict
        // from: the matrix has found cases where the optimizer silently elides the conversion
        // (tinyint/smallint vs real survive un-converted - their whole domain is exactly
        // representable in float) and cases where same-category-looking pairs never convert
        // the column at all (char vs varchar). A cell with no recorded probe is UNKNOWN, never
        // guessed from precedence direction - the precedence list is used elsewhere only to
        // decide operand *typing* (e.g. literal widening), never a verdict.
        var collationName = columnType.IsStringFamily ? columnType.Collation?.Name : null;

        var outcome = columnType.IsStringFamily && collationName is null
            // Collation is unresolved on the column, but not every string-family pair's
            // outcome actually depends on it: e.g. an nvarchar column vs a varchar value never
            // converts the column regardless of collation - a precedence-direction fact, not a
            // collation-dependent one. Only reuse an answer that every probed collation for
            // this pair agreed on; a pair where collation genuinely changes the outcome (e.g.
            // varchar column vs nvarchar value: ScanForced vs RangeSeek) still falls through to
            // UNKNOWN (CLAUDE.md: "collation unknown and unpinned by the manifest -> UNKNOWN").
            ? TypePairMatrix.Instance.TryGetOutcomeAgreeingAcrossCollations(columnType.Category, otherType.Category)
            : TypePairMatrix.Instance.TryGetOutcome(columnType.Category, otherType.Category, collationName);

        if (outcome is null || outcome.CompileFailed)
        {
            return Verdict.Unknown;
        }

        if (!outcome.ColumnConverts)
        {
            return Verdict.SeekPreserved;
        }

        return outcome.DynamicRangeSeekAvailable ? Verdict.RangeSeek : Verdict.ScanForced;
    }

    private static bool IsOutOfModelCategory(SqlTypeCategory category) =>
        category is SqlTypeCategory.SqlVariant or SqlTypeCategory.Xml or SqlTypeCategory.UserDefined
            or SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image;

    private static Verdict ClassifySameCategory(SqlType columnType, SqlType otherType, bool otherIsLiteral)
    {
        if (!columnType.IsStringFamily || columnType.Collation is null || otherType.Collation is null)
        {
            // Same category, no collation to conflict on (or collation unresolved on a
            // non-comparison-relevant side) - length/precision differences alone don't
            // defeat sargability (oracle-verified across varchar/nvarchar/decimal facet
            // pairs: every same-category, same-collation-status pair seeks cleanly).
            return Verdict.SeekPreserved;
        }

        if (string.Equals(columnType.Collation.Name, otherType.Collation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.SeekPreserved;
        }

        // Same string category, genuinely different, both-resolved collations. A literal is
        // always "coercible default" (never conflicts) and forces CONVERT_IMPLICIT onto the
        // column - oracle-confirmed ScanForced, never RangeSeek (the dynamic-range-seek
        // optimization is cross-category-only, never observed for a same-category collation
        // mismatch in any probed shape). Anything else (a real column, or a CAST/CONVERT/
        // function result whose own coercibility tier this pass has not verified) stays
        // Unknown rather than guess which of "compile error" or "silent convert" applies.
        return otherIsLiteral ? Verdict.ScanForced : Verdict.Unknown;
    }
}
