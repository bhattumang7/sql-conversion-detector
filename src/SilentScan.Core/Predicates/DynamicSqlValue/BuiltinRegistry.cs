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

    /// <summary>True when <paramref name="name"/> is a builtin this registry has its own spec for - lets a caller (the scalar-UDF catalog fallback in <see cref="ExpressionEvaluator"/>) distinguish "unrecognized name" from every other decline reason before it invests in a catalog lookup of its own.</summary>
    public static bool IsKnownBuiltin(string name) => Specs.ContainsKey(name);

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
    /// (the source isn't a concrete Text) accepts Char/NChar too, since it never needs a rendered
    /// value - CAST's own RESULT TYPE is a hard syntactic fact regardless of whether the source
    /// itself resolved to a clean typed Hole or couldn't resolve AT ALL (a MIXED literal+hole
    /// template, a Choice, ...): the source's own unresolvability only ever blocks computing the
    /// exact rendered VALUE, never the type CAST/CONVERT's own syntax already pins.
    /// </summary>
    public static BuiltinFoldResult FoldCastOrConvert(SqlType targetType, BuiltinArgument source, SourceSpan site)
    {
        if (targetType.Category is not (SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.Char or SqlTypeCategory.NChar))
        {
            return new BuiltinFoldResult.Fail("non-literal-expression:cast-target-not-pinned");
        }

        if (source is BuiltinArgument.Unresolved)
        {
            return BuiltinFoldResult.OkHole(targetType, site, HoleKind.ArgumentIndependentReturnType);
        }

        if (source is BuiltinArgument.Hole castHole)
        {
            return BuiltinFoldResult.OkHole(targetType, site, castHole.Kind);
        }

        if (targetType.Category is SqlTypeCategory.Char or SqlTypeCategory.NChar)
        {
            // Oracle-verified: CAST(x AS CHAR(n))/NCHAR(n) blank-pads a shorter input with
            // trailing spaces to exactly n characters, and truncates a longer one - the SAME
            // truncation rule VARCHAR/NVARCHAR already fold below, plus padding CHAR/NCHAR's own
            // fixed-length semantics VARCHAR/NVARCHAR don't have. Only when the target's own
            // length is actually pinned (an explicit CHAR(n)/NCHAR(n), not a bare unqualified
            // CHAR whose default length this resolver doesn't independently pin) - unpinned stays
            // declined exactly as before, never guessing the T-SQL default length.
            if (targetType.Length is not { } charLength)
            {
                return new BuiltinFoldResult.Fail("non-literal-expression:cast-target-not-pinned");
            }

            var charInput = ((BuiltinArgument.Text)source).Value;
            var charResult = charInput.Length > charLength ? charInput[..charLength] : charInput.PadRight(charLength);
            return BuiltinFoldResult.OkText(charResult, site);
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
        yield return Replicate();
        yield return Reverse();
        yield return QuoteName();
        yield return CharOrNChar("CHAR", maxCodePoint: 255, SqlTypeCategory.Char);
        yield return CharOrNChar("NCHAR", maxCodePoint: 65535, SqlTypeCategory.NChar);
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

        // Every session/environment name-lookup builtin below is oracle-verified
        // (sys.dm_exec_describe_first_result_set, compat 160) to return a fixed nvarchar(128) -
        // ORIGINAL_LOGIN() alone is nvarchar(4000) - regardless of the caller's own arguments (or
        // lack of them): these read session/server state, never anything this scanner could
        // possibly have folded from source text, so - same reasoning as SERVERPROPERTY -
        // EnvironmentDependent rather than NonDeterministicTyped. A common corpus pattern this
        // unlocks: dynamic SQL that splices `'USE [' + DB_NAME() + ']'` or an audit-trail message
        // built from USER_NAME()/APP_NAME()/HOST_NAME() - previously declined outright as an
        // unrecognized function name, now a typed hole the rest of the template can still resolve
        // around.
        yield return EnvironmentNameSpec("DB_NAME");
        yield return EnvironmentNameSpec("USER_NAME");
        yield return EnvironmentNameSpec("SUSER_SNAME");
        yield return EnvironmentNameSpec("SUSER_NAME");
        yield return EnvironmentNameSpec("APP_NAME");
        yield return EnvironmentNameSpec("HOST_NAME");
        yield return EnvironmentNameSpec("SCHEMA_NAME");
        yield return FixedTypeSpec("ORIGINAL_LOGIN", new SqlType(SqlTypeCategory.NVarChar, Length: 4000), HoleKind.EnvironmentDependent);

        // Oracle-verified (sys.dm_exec_describe_first_result_set, compat 160): every one of these
        // is a genuinely common corpus builtin found entirely missing from this registry auditing
        // a real production database - each one's absence meant EVERY dynamic-SQL variable it
        // touched declined outright as "non-literal-expression:function-call", not just that one
        // sub-expression. None gets a real Evaluate (computing the true value would mean modeling
        // real date/string arithmetic, a separate, larger effort) - a typed hole is still a
        // strict improvement over declining, and lets the surrounding template still resolve.
        yield return DateAdd();
        yield return FixedTypeSpec("DATEPART", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("DATEDIFF", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("CHARINDEX", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("DATALENGTH", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("LEN", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return StuffSpec();

        // Every ERROR_* function is only ever valid inside a CATCH block and SCOPE_IDENTITY()
        // reads the session's own last IDENTITY insert - both genuinely environment/execution-
        // context-dependent, same category as SERVERPROPERTY, never anything foldable from source
        // text.
        yield return FixedTypeSpec("ERROR_MESSAGE", new SqlType(SqlTypeCategory.NVarChar, Length: 4000), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_NUMBER", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_SEVERITY", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_STATE", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_LINE", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_PROCEDURE", new SqlType(SqlTypeCategory.NVarChar, Length: 128), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("SCOPE_IDENTITY", new SqlType(SqlTypeCategory.Decimal, Precision: 38, Scale: 0), HoleKind.EnvironmentDependent);
    }

    /// <summary>
    /// DATEADD's result type is NOT argument-independent like the others above: oracle-verified
    /// (same rule <see cref="Rules.BuiltinFunctionTypeResolver.ResolveDateAddResult"/> already
    /// applies for typed-predicate purposes, reused here for consistency) - passes through the
    /// third argument's own type when it's already date/time-family, else resolves to plain
    /// datetime (the engine implicitly converts a numeric/string date argument). When the third
    /// argument isn't even a typed Hole (fully Unresolved, or a concrete Text/Number this
    /// registry doesn't itself evaluate), the datetime default still holds - the ONLY way DATEADD
    /// returns something OTHER than datetime is when the caller already proved the date argument
    /// itself is a MORE specific date/time type.
    /// </summary>
    private static BuiltinSpec DateAdd() => new(
        "DATEADD",
        Evaluate: null,
        HoleTransfer: call => BuiltinFoldResult.OkHole(
            call.Arguments is [_, _, BuiltinArgument.Hole { Type: { } thirdArgumentType }]
                ? Rules.BuiltinFunctionTypeResolver.ResolveDateAddResult(thirdArgumentType)
                : new SqlType(SqlTypeCategory.DateTime),
            call.Site,
            HoleKind.ArgumentIndependentReturnType),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>
    /// STUFF(source, start, length, replacement) - oracle-verified: its result type follows the
    /// SOURCE argument (like REPLACE/SUBSTRING), never the replacement text. No Evaluate (unlike
    /// REPLACE/SUBSTRING): computing the real spliced value needs start/length arithmetic this
    /// registry doesn't otherwise model for this builtin - a typed-hole passthrough is still a
    /// strict improvement over the prior outright decline.
    /// </summary>
    private static BuiltinSpec StuffSpec() => new(
        "STUFF",
        Evaluate: null,
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static BuiltinSpec EnvironmentNameSpec(string name) =>
        FixedTypeSpec(name, new SqlType(SqlTypeCategory.NVarChar, Length: 128), HoleKind.EnvironmentDependent);

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
    /// The LEFT/RIGHT/SUBSTRING/REPLICATE shape: the result type passes through
    /// <paramref name="call"/>'s own first (source) argument regardless of whether a LATER
    /// length/count/start argument resolved - but when the source itself isn't a typed Hole
    /// (nothing to pass through), the decline should still prefer that OTHER argument's own
    /// <see cref="BuiltinArgument.Unresolved"/> reason (e.g. "variable-not-in-scope") over the
    /// generic fallback, since it is usually the more informative one - <paramref name="otherArguments"/>
    /// checked in the order given, source checked last.
    /// </summary>
    private static BuiltinFoldResult PassThroughSourceType(BuiltinCall call, params ReadOnlySpan<BuiltinArgument> otherArguments) =>
        call.Arguments[0] is BuiltinArgument.Hole hole
            ? BuiltinFoldResult.OkHole(hole.Type, call.Site, hole.Kind)
            : UnresolvedOrGeneric([.. otherArguments, call.Arguments[0]]);

    /// <summary>
    /// Oracle-verified: LEFT/RIGHT with a length at or beyond the input's own length return the
    /// whole string, no padding. A negative length is a distinct real-server error (Msg 536) this
    /// scanner has no representation for, so it declines rather than guessing at a runtime error.
    /// </summary>
    private static BuiltinSpec Left() => LeftOrRight("LEFT", (input, length) => input[..length]);

    private static BuiltinSpec Right() => LeftOrRight("RIGHT", (input, length) => input[^length..]);

    /// <summary>
    /// Oracle-verified: REPLICATE(string, count) repeats the string exactly <c>count</c> times -
    /// count=0 returns an empty string (not NULL), a NEGATIVE count returns SQL NULL, which this
    /// domain has no representation for, so it declines rather than guessing. A real corpus
    /// pattern (SQL-Server-First-Responder-Kit's sp_DatabaseRestore.sql) uses
    /// <c>REPLACE(text, N'''', REPLICATE(N'''', 4))</c> to quadruple an embedded quote when
    /// splicing a literal into dynamic SQL - REPLICATE was entirely unmodeled, so that whole
    /// REPLACE call declined with the generic "symbolic-value-in-function-argument" the moment
    /// it saw an unrecognized function name for its own replacement argument.
    /// </summary>
    private static BuiltinSpec Replicate() => new(
        "REPLICATE",
        Evaluate: call =>
        {
            var count = ((BuiltinArgument.Number)call.Arguments[1]).Value;
            if (count < 0)
            {
                return new BuiltinFoldResult.Fail("non-literal-expression:replicate-negative-count");
            }

            var input = ((BuiltinArgument.Text)call.Arguments[0]).Value;
            return BuiltinFoldResult.OkText(string.Concat(Enumerable.Repeat(input, count)), call.Site);
        },
        // The result TYPE (varchar/nvarchar of the source's own collation) never depends on the
        // count's own VALUE - only actually SLICING the string does (the Evaluate branch above,
        // which does require a concrete Number). So a hole/unresolved count still lets the type
        // pass through; only a count PROVEN negative is a real error (Msg 106) worth declining
        // for - an unresolved count can never be proven negative, so it never blocks this.
        HoleTransfer: call => call.Arguments[1] is BuiltinArgument.Number { Value: < 0 }
            ? new BuiltinFoldResult.Fail("non-literal-expression:replicate-negative-count")
            : PassThroughSourceType(call, call.Arguments[1]),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    /// <summary>Oracle-verified: REVERSE reverses a whole string with no length/collation sensitivity of its own (unlike CaseConversionSpec's Turkish-I concern - reversing character order never depends on how two characters COMPARE). Reverses UTF-16 code units, matching how SQL Server's own nvarchar storage operates - consistent for the ASCII/BMP text this scanner ever sees in a dynamic-SQL-building context.</summary>
    private static BuiltinSpec Reverse() => new(
        "REVERSE",
        Evaluate: call => BuiltinFoldResult.OkText(ReverseText(((BuiltinArgument.Text)call.Arguments[0]).Value), call.Site),
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static string ReverseText(string input)
    {
        var chars = input.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

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
        // Same reasoning as Replicate's own HoleTransfer above: LEFT/RIGHT's result type never
        // depends on the length's VALUE, only actually slicing does - a hole/unresolved length
        // still lets the source's type pass through; only a length PROVEN negative (Msg 536) is
        // worth declining for.
        HoleTransfer: call => call.Arguments[1] is BuiltinArgument.Number { Value: < 0 }
            ? new BuiltinFoldResult.Fail(NegativeLength)
            : PassThroughSourceType(call, call.Arguments[1]),
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
        // Same reasoning as LEFT/RIGHT's own HoleTransfer: the result type never depends on
        // start/length's own VALUES, only actually slicing does. Only a start/length PROVEN
        // negative or a start PROVEN below 1 (both real, modeled error/behavior cases) declines -
        // an unresolved start or length can never be proven either, so it never blocks this.
        HoleTransfer: call =>
        {
            if (call.Arguments[1] is BuiltinArgument.Number { Value: < 1 })
            {
                return new BuiltinFoldResult.Fail("non-literal-expression:substring-start-below-one");
            }

            return call.Arguments[2] is BuiltinArgument.Number { Value: < 0 }
                ? new BuiltinFoldResult.Fail(NegativeLength)
                : PassThroughSourceType(call, call.Arguments[1], call.Arguments[2]);
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
    private static BuiltinSpec CharOrNChar(string name, int maxCodePoint, SqlTypeCategory category) => new(
        name,
        Evaluate: call =>
        {
            var codePoint = ((BuiltinArgument.Number)call.Arguments[0]).Value;
            return codePoint is < 0 || codePoint > maxCodePoint
                ? new BuiltinFoldResult.Fail("non-literal-expression:char-out-of-range")
                : BuiltinFoldResult.OkText(((char)codePoint).ToString(), call.Site);
        },
        // Oracle-verified: CHAR/NCHAR always return exactly one character (or NULL for an
        // out-of-range code point, never an error) - the RESULT TYPE (char(1)/nchar(1)) is a hard
        // guarantee regardless of the code point argument's own value, the same "known shape,
        // unknown value" reasoning CAST/CONVERT's own target type already gets. Only the actual
        // rendered character (or whether it's NULL) depends on a concrete argument, never the type.
        HoleTransfer: call => BuiltinFoldResult.OkHole(new SqlType(category, Length: 1), call.Site, HoleKind.ArgumentIndependentReturnType),
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
