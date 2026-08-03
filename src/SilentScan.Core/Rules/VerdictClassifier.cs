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
    /// <param name="operatorText">
    /// The comparison operator's rendered text (e.g. "=", "LIKE"), or null when unknown/not
    /// applicable. The matrix's RangeSeek cells (<see cref="TypePairOutcome.DynamicRangeSeekAvailable"/>)
    /// were all originally probed with a single equality comparison against a variable - oracle-
    /// verified directly to NOT generalize to every operator/operand shape: a LIKE predicate
    /// whose pattern is not a literal (the pattern's shape is unknown at compile time, so the
    /// optimizer can't build the same range rewrite an equality or a literal-pattern LIKE gets)
    /// loses the dynamic range seek and is genuinely ScanForced instead. Only matters for cells
    /// that would otherwise be RangeSeek; every other outcome (SeekPreserved/ScanForced/
    /// OperandClash/Unknown) is operator-invariant in every pair sampled.
    /// </param>
    /// <remarks>
    /// A column-vs-column operand shape (e.g. a JOIN predicate) was investigated as a possible
    /// third correction alongside <paramref name="operatorText"/> - an initial sample (both sides
    /// indexed) showed the same RangeSeek-losing pattern the non-literal-LIKE case does. Further
    /// probing showed that result is confounded: whether <c>GetRangeThroughConvert</c> appears
    /// for a column-vs-column comparison depends on whether the OTHER column is ALSO indexed
    /// (both indexed: no dynamic range seek in the plan; only the classified side indexed - the
    /// far more common real-world shape - the dynamic range seek IS present, agreeing with the
    /// matrix). That makes it a plan-SHAPE-dependent fact in the sense CLAUDE.md's own oracle
    /// discipline warns against trusting ("Oracle is plan-XML based, never plan-shape based"),
    /// not a stable type-pair fact the matrix's (Category, Category, Collation) key could safely
    /// encode - a blanket column-vs-column correction would misclassify the common single-
    /// indexed-side join as ScanForced when it is genuinely RangeSeek. Deliberately NOT
    /// implemented; column-vs-column keeps the matrix's plain column-vs-variable-probed answer.
    /// </remarks>
    public static Verdict Classify(SqlType? columnType, SqlType? otherType, bool otherIsLiteral = false, string? operatorText = null) =>
        ClassifyWithReason(columnType, otherType, otherIsLiteral, operatorText).Verdict;

    /// <summary>
    /// Same classification as <see cref="Classify"/>, plus - only when the result is
    /// <see cref="Verdict.Unknown"/> - a short, stable reason code naming WHICH of this method's
    /// three distinct Unknown-producing branches fired: <c>"operand-type-unresolved"</c> (one or
    /// both sides never resolved a type at all), <c>"out-of-model-category:{category}"</c>
    /// (sql_variant/xml/UDT/text-family, CLAUDE.md's own named hard cases), or
    /// <c>"no-probed-matrix-cell"</c> (a real, in-model cross-category pair the oracle matrix has
    /// simply never been asked about). Null for every non-Unknown verdict - CLAUDE.md's "Unknown,
    /// never a guess" discipline is about the verdict itself; a reason code only exists to explain
    /// an Unknown once it's already been reached, never to justify inventing one.
    /// </summary>
    public static (Verdict Verdict, string? UnknownReason) ClassifyWithReason(SqlType? columnType, SqlType? otherType, bool otherIsLiteral = false, string? operatorText = null)
    {
        if (columnType is null || otherType is null)
        {
            return (Verdict.Unknown, "operand-type-unresolved");
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
        if (IsOutOfModelCategory(columnType.Category))
        {
            return (Verdict.Unknown, $"out-of-model-category:{columnType.Category}");
        }

        if (IsOutOfModelCategory(otherType.Category))
        {
            return (Verdict.Unknown, $"out-of-model-category:{otherType.Category}");
        }

        // A genuine, resolved collation mismatch between two string-family operands that are
        // BOTH "implicit" coercibility (a real column, or a CAST/CONVERT result that inherited
        // its input's collation with no explicit COLLATE of its own) does not compile at all
        // (Msg 468, oracle-verified) - independent of type CATEGORY. `CHAR` vs `VARCHAR` with
        // differing collations fails identically to `VARCHAR` vs `VARCHAR` with differing
        // collations, and a CAST result carrying a foreign column's collation conflicts with a
        // target column exactly like a second real column would (both probed directly). Checked
        // before the category split below so it applies uniformly to same-category AND
        // cross-category pairs - reporting a routine SeekPreserved/ScanForced verdict for a
        // predicate that does not compile at all would be worse than an Unknown. A literal is
        // always "coercible default" (never conflicts) and is excluded here; it is handled by
        // <see cref="ClassifySameCategory"/>'s own literal branch instead.
        if (!otherIsLiteral
            && columnType.IsStringFamily && otherType.IsStringFamily
            && columnType.Collation is { } columnCollation && otherType.Collation is { } otherCollation
            && !string.Equals(columnCollation.Name, otherCollation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return (Verdict.OperandClash, null);
        }

        if (columnType.Category == otherType.Category)
        {
            return (ClassifySameCategory(columnType, otherType), null);
        }

        return ClassifyCrossCategory(columnType, otherType, otherIsLiteral, operatorText);
    }

    /// <summary>
    /// Every cross-category pair - same family (int vs bigint, char vs nvarchar, date vs
    /// datetime, ...) or cross-family (varchar column vs int/datetime/guid value, and the
    /// reverse) - is decided by ONE authority: the Docker-oracle-probed matrix. The official
    /// precedence list is not reliable enough on its own to report a verdict from: the matrix has
    /// found cases where the optimizer silently elides the conversion (tinyint/smallint vs real
    /// survive un-converted - their whole domain is exactly representable in float) and cases
    /// where same-category-looking pairs never convert the column at all (char vs varchar). A
    /// cell with no recorded probe is UNKNOWN, never guessed from precedence direction - the
    /// precedence list is used elsewhere only to decide operand *typing* (e.g. literal widening),
    /// never a verdict. A genuine collation MISMATCH between two resolved, non-literal
    /// string-family operands was already routed to OperandClash by the caller, so by
    /// construction the only reason <paramref name="columnType"/>'s own collation matters here is
    /// to pick which probed collation family's matrix column applies - <paramref name="otherType"/>'s
    /// collation is irrelevant from this point on (either it agrees with columnType's, or the
    /// other operand is a literal, which never conflicts).
    /// </summary>
    private static (Verdict Verdict, string? UnknownReason) ClassifyCrossCategory(SqlType columnType, SqlType otherType, bool otherIsLiteral, string? operatorText)
    {
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

        if (outcome is null)
        {
            return (Verdict.Unknown, "no-probed-matrix-cell");
        }

        if (outcome.CompileFailed)
        {
            // A probed, empirically-confirmed fact (Roadmap Phase A3), not an absence of data -
            // distinct from the "no probe recorded this cell" Unknown case just above.
            return (Verdict.OperandClash, null);
        }

        if (!outcome.ColumnConverts)
        {
            return (Verdict.SeekPreserved, null);
        }

        if (!outcome.DynamicRangeSeekAvailable)
        {
            return (Verdict.ScanForced, null);
        }

        // The matrix cell says RangeSeek (probed as column vs. a variable, under `=`) - a LIKE
        // predicate whose pattern is not a literal loses that dynamic range seek and falls back
        // to ScanForced, the genuinely worse outcome (oracle-confirmed on the flagship
        // VarChar/NVarChar Windows-collation pair). Never turns a ScanForced cell INTO a
        // RangeSeek. See Classify's own remarks for why an analogous correction is NOT made for
        // a column-vs-column operand shape.
        var isNonLiteralLike = string.Equals(operatorText, "LIKE", StringComparison.Ordinal) && !otherIsLiteral;
        return (isNonLiteralLike ? Verdict.ScanForced : Verdict.RangeSeek, null);
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
            // defeat sargability (oracle-verified across varchar/nvarchar/decimal facet
            // pairs: every same-category, same-collation-status pair seeks cleanly).
            return Verdict.SeekPreserved;
        }

        if (string.Equals(columnType.Collation.Name, otherType.Collation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.SeekPreserved;
        }

        // Same string category, genuinely different, both-resolved collations. Classify's own
        // early check (above the category split) already returns OperandClash for any
        // non-literal instance of this - the only way to reach this point with a collation
        // mismatch still on the table is otherIsLiteral being true. A literal is always
        // "coercible default" (never conflicts) and forces CONVERT_IMPLICIT onto the column -
        // oracle-confirmed ScanForced, never RangeSeek (the dynamic-range-seek optimization is
        // cross-category-only, never observed for a same-category collation mismatch in any
        // probed shape).
        return Verdict.ScanForced;
    }
}
