using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// Classifies a column-vs-other comparison per CLAUDE.md's type rules: only column-side
/// conversion loses the seek; collation family determines SCAN_FORCED vs RANGE_SEEK for
/// string-family conversions; unresolved collation is UNKNOWN, never a guess.
/// </summary>
public static class VerdictClassifier
{
    public static Verdict Classify(SqlType? columnType, SqlType? otherType)
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
            return ClassifySameCategory(columnType, otherType);
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

    private static Verdict ClassifySameCategory(SqlType columnType, SqlType otherType)
    {
        if (!columnType.IsStringFamily || columnType.Collation is null || otherType.Collation is null)
        {
            // Same category, no collation to conflict on (or collation unresolved on a
            // non-comparison-relevant side) - length/precision differences alone don't
            // defeat sargability.
            return Verdict.SeekPreserved;
        }

        if (string.Equals(columnType.Collation.Name, otherType.Collation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.SeekPreserved;
        }

        // Same string category, genuinely different collations: T-SQL's coercibility
        // precedence rules for resolving which collation wins are a distinct, non-trivial
        // rule set this pass does not implement (CLAUDE.md precision discipline: never
        // guess).
        return Verdict.Unknown;
    }
}
