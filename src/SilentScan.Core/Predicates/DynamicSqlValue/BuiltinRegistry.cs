using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// Declarative per-builtin knowledge - replaces the old scanner's ten <c>TryFold*</c> methods
/// plus its five separate classification sets (<c>WhitelistedStringBuilders</c>,
/// <c>NonDeterministicFunctions</c>, <c>PlaceholderProducingNonDeterministicFunctions</c>,
/// <c>EnvironmentDependentFunctions</c>, <c>PlaceholderTypeTransfer</c>). Every fact about a
/// builtin lives in exactly one <see cref="BuiltinSpec"/> row; <see cref="Fold"/> is the single
/// dispatcher every caller uses. LEFT/RIGHT/CAST/CONVERT are ScriptDOM's own dedicated node
/// types, not <c>FunctionCall</c> - callers dispatch to <see cref="Left"/>/<see cref="Right"/> by
/// name anyway (this registry has no ScriptDOM dependency) and to
/// <see cref="FoldCastOrConvert"/> directly, since a CAST/CONVERT target type is pinned by call-
/// site syntax, never looked up by function name. See docs/dynamic-sql-rebuild-plan.md §3.
/// </summary>
public static class BuiltinRegistry
{
    private const string NegativeLength = "non-literal-expression:negative-length";
    private const string SymbolicValueInFunctionArgument = "symbolic-value-in-function-argument";
    private const string NonLiteralFunctionCall = "non-literal-expression:function-call";

    /// <summary>Every builtin this registry knows about, keyed case-insensitively - the single source of truth <see cref="Fold"/> dispatches through.</summary>
    private static readonly Dictionary<string, BuiltinSpec> Specs = BuildSpecs().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves one <see cref="BuiltinCall"/> against its registered <see cref="BuiltinSpec"/>.
    /// Order: (1) an unrecognized function name declines immediately - with NO spec to consult,
    /// this registry genuinely has no return-type fact to fall back on, so guessing one would
    /// violate CLAUDE.md's "never guess" policy regardless of whether an argument is unresolved
    /// too; (2) a spec whose value is unconditionally unknowable
    /// (<see cref="BuiltinSpec.UnconditionalFailReason"/>) declines with that reason regardless of
    /// arguments; (3) every argument resolved to a concrete
    /// <see cref="BuiltinArgument.Text"/>/<see cref="BuiltinArgument.Number"/> and the spec has an
    /// <see cref="BuiltinSpec.Evaluate"/> → run it; (4) the spec has a
    /// <see cref="BuiltinSpec.HoleTransfer"/> → run it UNCONDITIONALLY, even when an argument is
    /// <see cref="BuiltinArgument.Unresolved"/> rather than a typed <see cref="BuiltinArgument.Hole"/>
    /// - a builtin whose return type does not actually depend on that argument's value (QUOTENAME's
    /// nvarchar(258), STR's CHAR(n)) can still resolve; one that DOES need the argument's own type
    /// (case conversion, LEFT/RIGHT, SUBSTRING passthrough) declines from inside its own
    /// HoleTransfer, preserving that argument's OWN specific reason rather than a generic one; (5)
    /// the spec has a fixed <see cref="BuiltinSpec.ReturnType"/> (GETDATE, NEWID, RAND, ... -
    /// unconditional, regardless of arguments) → a typed hole of that type; (6) otherwise decline,
    /// preferring the first <see cref="BuiltinArgument.Unresolved"/> argument's own reason over the
    /// generic fallback when one is present.
    /// </summary>
    public static BuiltinFoldResult Fold(BuiltinCall call)
    {
        if (!Specs.TryGetValue(call.FunctionName, out var spec))
        {
            return new BuiltinFoldResult.Fail(NonLiteralFunctionCall);
        }

        if (spec.UnconditionalFailReason is { } reason)
        {
            return new BuiltinFoldResult.Fail(reason);
        }

        var allConcrete = call.Arguments.All(a => a is BuiltinArgument.Text or BuiltinArgument.Number);
        if (allConcrete && spec.Evaluate is not null)
        {
            return spec.Evaluate(call);
        }

        if (spec.HoleTransfer is not null)
        {
            return spec.HoleTransfer(call);
        }

        if (spec.ReturnType is { } returnType)
        {
            return BuiltinFoldResult.OkHole(returnType, call.Site, spec.ReturnKind);
        }

        var firstUnresolved = call.Arguments.OfType<BuiltinArgument.Unresolved>().FirstOrDefault();
        return new BuiltinFoldResult.Fail(firstUnresolved?.Reason
            ?? (call.Arguments.Any(a => a is BuiltinArgument.Hole) ? SymbolicValueInFunctionArgument : NonLiteralFunctionCall));
    }

    /// <summary>
    /// CAST/CONVERT's target type is pinned by the call site's own syntax, never looked up by
    /// function name - the one fold with no <see cref="BuiltinSpec"/> entry, called directly by
    /// whatever resolves a <c>DataTypeReference</c> to a <see cref="SqlType"/>. The VALUE-rendering
    /// fold (truncation) only works for a VarChar/NVarChar target (oracle-verified: silently
    /// truncates over-length input, no error) - Char/NChar's blank-padding rendering isn't modeled,
    /// so those decline the value fold even for a concrete Text source, but the TYPE-transfer fold
    /// (the source is a Hole) accepts Char/NChar too, since a hole never needs a rendered value.
    /// </summary>
    public static BuiltinFoldResult FoldCastOrConvert(SqlType targetType, BuiltinArgument source, SourceSpan site)
    {
        if (targetType.Category is not (SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.Char or SqlTypeCategory.NChar))
        {
            return new BuiltinFoldResult.Fail("non-literal-expression:cast-target-not-pinned");
        }

        if (source is BuiltinArgument.Unresolved unresolved)
        {
            return new BuiltinFoldResult.Fail(unresolved.Reason);
        }

        if (source is BuiltinArgument.Hole castHole)
        {
            return BuiltinFoldResult.OkHole(targetType, site, castHole.Kind);
        }

        if (targetType.Category is SqlTypeCategory.Char or SqlTypeCategory.NChar)
        {
            return new BuiltinFoldResult.Fail("non-literal-expression:cast-target-not-pinned");
        }

        var input = ((BuiltinArgument.Text)source).Value;
        var result = !targetType.IsMax && targetType.Length is { } length && input.Length > length ? input[..length] : input;
        return BuiltinFoldResult.OkText(result, site);
    }

    private static IEnumerable<BuiltinSpec> BuildSpecs()
    {
        yield return CaseConversionSpec("UPPER", s => s.ToUpperInvariant());
        yield return CaseConversionSpec("LOWER", s => s.ToLowerInvariant());
        yield return TrimSpec("LTRIM", s => s.TrimStart(' '));
        yield return TrimSpec("RTRIM", s => s.TrimEnd(' '));
        yield return Left();
        yield return Right();
        yield return Substring();
        yield return Replace();
        yield return QuoteName();
        yield return CharOrNChar("CHAR", maxCodePoint: 255);
        yield return CharOrNChar("NCHAR", maxCodePoint: 65535);
        yield return Str();

        yield return NonDeterministicTyped("NEWID", new SqlType(SqlTypeCategory.UniqueIdentifier));
        yield return NonDeterministicTyped("NEWSEQUENTIALID", new SqlType(SqlTypeCategory.UniqueIdentifier));
        yield return NonDeterministicTyped("GETDATE", new SqlType(SqlTypeCategory.DateTime));
        yield return NonDeterministicTyped("GETUTCDATE", new SqlType(SqlTypeCategory.DateTime));
        yield return NonDeterministicTyped("SYSDATETIME", new SqlType(SqlTypeCategory.DateTime2));
        yield return NonDeterministicTyped("SYSUTCDATETIME", new SqlType(SqlTypeCategory.DateTime2));
        yield return NonDeterministicTyped("SYSDATETIMEOFFSET", new SqlType(SqlTypeCategory.DateTimeOffset));
        yield return NonDeterministicTyped("RAND", new SqlType(SqlTypeCategory.Float));
        yield return NonDeterministicTyped("CHECKSUM", new SqlType(SqlTypeCategory.Int));
        yield return NonDeterministicTyped("BINARY_CHECKSUM", new SqlType(SqlTypeCategory.Int));

        // SERVERPROPERTY's own return type (sql_variant, per T-SQL docs) is a hard guarantee
        // regardless of which property name is requested - the VALUE is what depends on the
        // session/server environment, not the type, so this is EnvironmentDependent (not
        // NonDeterministicTyped: re-running the SAME call on the SAME server returns the SAME
        // value, unlike NEWID/GETDATE - it only varies ACROSS servers/sessions).
        yield return FixedTypeSpec("SERVERPROPERTY", new SqlType(SqlTypeCategory.SqlVariant), HoleKind.EnvironmentDependent);
    }

    /// <summary>
    /// Oracle-verified (Turkish_CI_AS vs Latin1_General_CI_AS): every ASCII letter except 'i'/'I'
    /// case-converts identically across every collation; 'i'/'I' genuinely differs by collation
    /// (the Turkish "dotless I" problem), and this registry has no collation context, so an input
    /// containing 'i'/'I' or any non-ASCII character declines rather than guessing.
    /// </summary>
    private static BuiltinSpec CaseConversionSpec(string name, Func<string, string> convert) => new(
        name,
        Evaluate: call =>
        {
            var input = ((BuiltinArgument.Text)call.Arguments[0]).Value;
            return IsSafeToCaseConvert(input)
                ? BuiltinFoldResult.OkText(convert(input), call.Site)
                : new BuiltinFoldResult.Fail("non-literal-expression:case-conversion-collation-sensitive");
        },
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static bool IsSafeToCaseConvert(string input) => input.All(c => c is not ('i' or 'I') && c <= 127);

    /// <summary>The FIRST <see cref="BuiltinArgument.Unresolved"/> reason found across <paramref name="arguments"/>, or the generic <see cref="SymbolicValueInFunctionArgument"/> fallback if none - used by every <c>HoleTransfer</c> below whose own type genuinely depends on an argument this registry couldn't resolve, so the decline reports WHY that argument was unresolvable rather than a reason-free "symbolic value" label.</summary>
    private static BuiltinFoldResult.Fail UnresolvedOrGeneric(IEnumerable<BuiltinArgument> arguments) =>
        arguments.OfType<BuiltinArgument.Unresolved>().FirstOrDefault() is { } unresolved
            ? new BuiltinFoldResult.Fail(unresolved.Reason)
            : new BuiltinFoldResult.Fail(SymbolicValueInFunctionArgument);

    /// <summary>Oracle-verified: LTRIM/RTRIM trim only the space character (0x20) - a tab or other whitespace is left untouched, unlike .NET's parameterless Trim family.</summary>
    private static BuiltinSpec TrimSpec(string name, Func<string, string> trim) => new(
        name,
        Evaluate: call => BuiltinFoldResult.OkText(trim(((BuiltinArgument.Text)call.Arguments[0]).Value), call.Site),
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static BuiltinFoldResult PassThroughSingleArgumentType(BuiltinCall call) =>
        call.Arguments[0] is BuiltinArgument.Hole hole
            ? BuiltinFoldResult.OkHole(hole.Type, call.Site, hole.Kind)
            : UnresolvedOrGeneric([call.Arguments[0]]);

    /// <summary>
    /// Oracle-verified: LEFT/RIGHT with a length at or beyond the input's own length return the
    /// whole string, no padding. A negative length is a distinct real-server error (Msg 536) this
    /// scanner has no representation for, so it declines rather than guessing at a runtime error.
    /// </summary>
    private static BuiltinSpec Left() => LeftOrRight("LEFT", (input, length) => input[..length]);

    private static BuiltinSpec Right() => LeftOrRight("RIGHT", (input, length) => input[^length..]);

    private static BuiltinSpec LeftOrRight(string name, Func<string, int, string> slice) => new(
        name,
        Evaluate: call =>
        {
            var length = ((BuiltinArgument.Number)call.Arguments[1]).Value;
            if (length < 0)
            {
                return new BuiltinFoldResult.Fail(NegativeLength);
            }

            var input = ((BuiltinArgument.Text)call.Arguments[0]).Value;
            return BuiltinFoldResult.OkText(slice(input, Math.Min(length, input.Length)), call.Site);
        },
        HoleTransfer: call =>
        {
            if (call.Arguments[1] is not BuiltinArgument.Number lengthArgument)
            {
                return UnresolvedOrGeneric([call.Arguments[1]]);
            }

            return lengthArgument.Value < 0 ? new BuiltinFoldResult.Fail(NegativeLength) : PassThroughSingleArgumentType(call);
        },
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>
    /// Oracle-verified: SUBSTRING clamps a length-beyond-the-end down to whatever remains, and a
    /// start beyond the input's length returns empty rather than erroring. A negative length is
    /// Msg 536 (declined, like LEFT/RIGHT); a start below 1 is real, defined T-SQL behavior this
    /// registry does not model (rare enough outside adversarial input to decline rather than add
    /// the extra clipping arithmetic for it).
    /// </summary>
    private static BuiltinSpec Substring() => new(
        "SUBSTRING",
        Evaluate: call =>
        {
            var (start, length, failure) = SubstringArgs(call);
            if (failure is { } f)
            {
                return f;
            }

            var input = ((BuiltinArgument.Text)call.Arguments[0]).Value;
            if (start > input.Length)
            {
                return BuiltinFoldResult.OkText(string.Empty, call.Site);
            }

            var clampedLength = Math.Min(length, input.Length - (start - 1));
            return BuiltinFoldResult.OkText(input.Substring(start - 1, clampedLength), call.Site);
        },
        HoleTransfer: call =>
        {
            if (call.Arguments[1] is not BuiltinArgument.Number || call.Arguments[2] is not BuiltinArgument.Number)
            {
                return UnresolvedOrGeneric([call.Arguments[1], call.Arguments[2]]);
            }

            var (_, _, failure) = SubstringArgs(call);
            return failure ?? PassThroughSingleArgumentType(call);
        },
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static (int Start, int Length, BuiltinFoldResult.Fail? Failure) SubstringArgs(BuiltinCall call)
    {
        var start = ((BuiltinArgument.Number)call.Arguments[1]).Value;
        var length = ((BuiltinArgument.Number)call.Arguments[2]).Value;
        if (length < 0)
        {
            return (start, length, new BuiltinFoldResult.Fail(NegativeLength));
        }

        if (start < 1)
        {
            return (start, length, new BuiltinFoldResult.Fail("non-literal-expression:substring-start-below-one"));
        }

        return (start, length, null);
    }

    /// <summary>
    /// REPLACE folds a fully-concrete call when a strictly-ordinal replace and an ordinal-IGNORE-
    /// CASE replace agree (oracle-verified generalization of <see cref="IsSafeToCaseConvert"/>'s
    /// own reasoning: if neither extreme of case-matching changes the answer, no collation this
    /// registry has never seen can produce a third answer). When only the SOURCE is a hole, the
    /// hole's own type passes through unchanged - REPLACE cannot alter its source's type
    /// regardless of pattern/replacement. When source AND pattern are both concrete but the
    /// REPLACEMENT is a hole, the literal template's SHAPE is still fully known (every occurrence
    /// of a concrete pattern in a concrete source is a known split), so the call splices the
    /// hole between the surrounding literal pieces instead of declining outright.
    /// </summary>
    private static BuiltinSpec Replace() => new(
        "REPLACE",
        Evaluate: ReplaceEvaluate,
        HoleTransfer: ReplaceHoleTransfer,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>Extracted from <see cref="Replace"/>'s <c>Evaluate</c> delegate solely to keep the enclosing factory method's own Cognitive Complexity (Sonar S3776) under the two nested-lambda bodies it previously carried.</summary>
    private static BuiltinFoldResult ReplaceEvaluate(BuiltinCall call)
    {
        if (call.Arguments.Count != 3)
        {
            return new BuiltinFoldResult.Fail(NonLiteralFunctionCall);
        }

        var source = ((BuiltinArgument.Text)call.Arguments[0]).Value;
        var pattern = ((BuiltinArgument.Text)call.Arguments[1]).Value;
        var replacement = ((BuiltinArgument.Text)call.Arguments[2]).Value;
        if (pattern.Length == 0)
        {
            return new BuiltinFoldResult.Fail("non-literal-expression:replace-empty-pattern");
        }

        var ordinal = source.Replace(pattern, replacement, StringComparison.Ordinal);
        var ordinalIgnoreCase = source.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);
        return string.Equals(ordinal, ordinalIgnoreCase, StringComparison.Ordinal)
            ? BuiltinFoldResult.OkText(ordinal, call.Site)
            : new BuiltinFoldResult.Fail("non-literal-expression:replace-collation-sensitive");
    }

    /// <summary>Extracted from <see cref="Replace"/>'s <c>HoleTransfer</c> delegate for the same Cognitive Complexity reason as <see cref="ReplaceEvaluate"/>.</summary>
    private static BuiltinFoldResult ReplaceHoleTransfer(BuiltinCall call)
    {
        if (call.Arguments.Count != 3)
        {
            return new BuiltinFoldResult.Fail(NonLiteralFunctionCall);
        }

        if (call.Arguments[0] is BuiltinArgument.Hole sourceHole)
        {
            return BuiltinFoldResult.OkHole(sourceHole.Type, call.Site, sourceHole.Kind);
        }

        if (call.Arguments[0] is BuiltinArgument.Text source
            && call.Arguments[1] is BuiltinArgument.Text pattern
            && call.Arguments[2] is BuiltinArgument.Hole replacementHole)
        {
            if (pattern.Value.Length == 0)
            {
                return new BuiltinFoldResult.Fail("non-literal-expression:replace-empty-pattern");
            }

            return SpliceHoleIntoTemplate(source.Value, pattern.Value, replacementHole, call.Site);
        }

        return UnresolvedOrGeneric(call.Arguments);
    }

    private static BuiltinFoldResult.Ok SpliceHoleIntoTemplate(string source, string pattern, BuiltinArgument.Hole replacement, SourceSpan site)
    {
        var parts = source.Split(pattern, StringSplitOptions.None);
        var pieces = new List<TemplatePiece>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                pieces.Add(new TemplatePiece.Lit(parts[i], site, PrefixLength: 0));
            }

            if (i < parts.Length - 1)
            {
                pieces.Add(new TemplatePiece.Hole(replacement.Type, site, replacement.Kind));
            }
        }

        if (pieces.Count == 0)
        {
            pieces.Add(new TemplatePiece.Lit(string.Empty, site, PrefixLength: 0));
        }

        return new BuiltinFoldResult.Ok(pieces);
    }

    /// <summary>QUOTENAME always returns nvarchar(258) regardless of input length or delimiter - oracle-verified (SQL_VARIANT_PROPERTY MaxLength = 516 bytes = 258 UTF-16 code units).</summary>
    private static BuiltinSpec QuoteName() => new(
        "QUOTENAME",
        Evaluate: call =>
        {
            if (call.Arguments.Count is not (1 or 2))
            {
                return new BuiltinFoldResult.Fail(NonLiteralFunctionCall);
            }

            var input = ((BuiltinArgument.Text)call.Arguments[0]).Value;
            var delimiter = call.Arguments.Count == 2 ? ((BuiltinArgument.Text)call.Arguments[1]).Value : null;
            var quoted = QuoteNameValue(input, delimiter);

            // Oracle-verified: QUOTENAME returns SQL NULL for an input over 128 characters or an
            // unrecognized delimiter - concatenating NULL propagates NULL through the whole
            // @sql build, a materially different runtime outcome this registry cannot represent.
            return quoted is null
                ? new BuiltinFoldResult.Fail("non-literal-expression:quotename-null-result")
                : BuiltinFoldResult.OkText(quoted, call.Site);
        },
        // QUOTENAME's return TYPE is nvarchar(258) regardless of whether its input/delimiter
        // resolved to a concrete value, a typed hole, or genuinely couldn't be resolved at all -
        // only the runtime VALUE (real text vs. SQL NULL on an over-length/bad-delimiter input)
        // depends on that, and this is a type-transfer, not a value fold. Propagates the input
        // argument's own Kind when it IS a Hole (so provenance survives, matching every other
        // passthrough here); falls back to ArgumentIndependentReturnType when it's Unresolved,
        // since there is no argument-derived Kind left to propagate in that case.
        HoleTransfer: call => QuoteNameHoleTransfer(call),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>Extracted from QUOTENAME's <c>HoleTransfer</c> delegate solely to turn its nested ternary (Sonar S3358) into a named, independently-readable statement.</summary>
    private static BuiltinFoldResult QuoteNameHoleTransfer(BuiltinCall call)
    {
        if (call.Arguments.Count is not (1 or 2))
        {
            return new BuiltinFoldResult.Fail(NonLiteralFunctionCall);
        }

        var kind = call.Arguments[0] is BuiltinArgument.Hole quoteNameHole
            ? quoteNameHole.Kind
            : HoleKind.ArgumentIndependentReturnType;
        return BuiltinFoldResult.OkHole(new SqlType(SqlTypeCategory.NVarChar, Length: 258), call.Site, kind);
    }

    private static string? QuoteNameValue(string input, string? delimiter)
    {
        if (input.Length > 128)
        {
            return null;
        }

        var (open, close) = delimiter switch
        {
            null or "" or "[" or "]" => ('[', ']'),
            "(" or ")" => ('(', ')'),
            "<" or ">" => ('<', '>'),
            "{" or "}" => ('{', '}'),
            "'" => ('\'', '\''),
            "\"" => ('"', '"'),
            _ => (default(char), default(char)),
        };

        if (open == default)
        {
            return null;
        }

        var escaped = input.Replace(close.ToString(), $"{close}{close}", StringComparison.Ordinal);
        return $"{open}{escaped}{close}";
    }

    /// <summary>
    /// NCHAR(n)/CHAR(n) are pure constant-value functions whenever their integer argument folds
    /// to a literal (oracle-verified ranges and the NULL-outside-range behavior via DATALENGTH/
    /// SQL_VARIANT_PROPERTY - CHAR(0) is NOT null). No hole-transfer: unlike every other entry
    /// here, the old scanner never attempted one for CHAR/NCHAR, since their sole argument is
    /// always an integer expression, never a value this scanner's string-hole model covers.
    /// </summary>
    private static BuiltinSpec CharOrNChar(string name, int maxCodePoint) => new(
        name,
        Evaluate: call =>
        {
            var codePoint = ((BuiltinArgument.Number)call.Arguments[0]).Value;
            return codePoint is < 0 || codePoint > maxCodePoint
                ? new BuiltinFoldResult.Fail("non-literal-expression:char-out-of-range")
                : BuiltinFoldResult.OkText(((char)codePoint).ToString(), call.Site);
        },
        HoleTransfer: null,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>
    /// STR(float_expr [, length [, decimal]]) always returns a fixed-length CHAR value (CHAR(10)
    /// when length/decimal are omitted, oracle-verified) regardless of the input's runtime value -
    /// the same "target type pinned by the call site's own syntax" reasoning CAST/CONVERT use.
    /// STR's actual numeric-rendering algorithm (rounding/padding/overflow-to-'*') is never
    /// modeled - the only fold this ever produces is the type-transfer; even a concrete,
    /// non-hole float_expr declines rather than guessing a rendered value.
    /// </summary>
    private static BuiltinSpec Str() => new(
        "STR",
        Evaluate: _ => new BuiltinFoldResult.Fail(NonLiteralFunctionCall),
        HoleTransfer: call =>
        {
            int length;
            if (call.Arguments.Count >= 2)
            {
                if (call.Arguments[1] is not BuiltinArgument.Number lengthArgument)
                {
                    return UnresolvedOrGeneric([call.Arguments[1]]);
                }

                length = lengthArgument.Value;
            }
            else
            {
                length = 10;
            }

            if (length < 1)
            {
                return new BuiltinFoldResult.Fail("non-literal-expression:str-length-out-of-range");
            }

            // STR's length is pinned by the call site's own syntax (or the CHAR(10) default),
            // exactly like CAST/CONVERT's target type - the RETURN type is fixed regardless of
            // whether float_expr resolved to a typed hole or couldn't be resolved at all.
            return call.Arguments[0] switch
            {
                BuiltinArgument.Hole strHole => BuiltinFoldResult.OkHole(new SqlType(SqlTypeCategory.Char, Length: length), call.Site, strHole.Kind),
                BuiltinArgument.Unresolved => BuiltinFoldResult.OkHole(new SqlType(SqlTypeCategory.Char, Length: length), call.Site, HoleKind.ArgumentIndependentReturnType),
                _ => new BuiltinFoldResult.Fail(NonLiteralFunctionCall),
            };
        },
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>A builtin whose VALUE is unknowable at compile time but whose RETURN TYPE is a hard T-SQL guarantee regardless of which call produced it - the same "known shape, unknown value" case an uninitialized DECLARE gets. NEWID/GETDATE/RAND/... all report <see cref="HoleKind.NonDeterministicTyped"/> through this.</summary>
    private static BuiltinSpec NonDeterministicTyped(string name, SqlType returnType) => FixedTypeSpec(name, returnType, HoleKind.NonDeterministicTyped);

    /// <summary>A builtin that takes no arguments whose resolution matters and whose return type is a hard T-SQL guarantee regardless - <paramref name="kind"/> lets a caller distinguish WHY the value itself is still unknowable (non-determinism vs. environment-dependence) while sharing the exact same dispatch (step 5 of <see cref="Fold"/>).</summary>
    private static BuiltinSpec FixedTypeSpec(string name, SqlType returnType, HoleKind kind) => new(
        name, Evaluate: null, HoleTransfer: null, ReturnType: returnType, ReturnKind: kind, UnconditionalFailReason: null);
}

/// <summary>One registered builtin's complete folding knowledge - see <see cref="BuiltinRegistry.Fold"/> for the dispatch order.</summary>
public sealed record BuiltinSpec(
    string Name,
    Func<BuiltinCall, BuiltinFoldResult>? Evaluate,
    Func<BuiltinCall, BuiltinFoldResult>? HoleTransfer,
    SqlType? ReturnType,
    HoleKind ReturnKind,
    string? UnconditionalFailReason);
