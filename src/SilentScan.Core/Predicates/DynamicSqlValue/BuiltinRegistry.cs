using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public static class BuiltinRegistry
{
    private const string NegativeLength = "non-literal-expression:negative-length";
    private const string SymbolicValueInFunctionArgument = "symbolic-value-in-function-argument";
    private const string NonLiteralFunctionCall = "non-literal-expression:function-call";

    private static readonly Dictionary<string, BuiltinSpec> Specs = BuildSpecs().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownBuiltin(string name) => Specs.ContainsKey(name);

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

        yield return FixedTypeSpec("SERVERPROPERTY", new SqlType(SqlTypeCategory.SqlVariant), HoleKind.EnvironmentDependent);

        yield return EnvironmentNameSpec("DB_NAME");
        yield return EnvironmentNameSpec("USER_NAME");
        yield return EnvironmentNameSpec("SUSER_SNAME");
        yield return EnvironmentNameSpec("SUSER_NAME");
        yield return EnvironmentNameSpec("APP_NAME");
        yield return EnvironmentNameSpec("HOST_NAME");
        yield return EnvironmentNameSpec("SCHEMA_NAME");
        yield return FixedTypeSpec("ORIGINAL_LOGIN", new SqlType(SqlTypeCategory.NVarChar, Length: 4000), HoleKind.EnvironmentDependent);

        yield return DateAdd();
        yield return FixedTypeSpec("DATEPART", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("DATEDIFF", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("CHARINDEX", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("DATALENGTH", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return FixedTypeSpec("LEN", new SqlType(SqlTypeCategory.Int), HoleKind.ArgumentIndependentReturnType);
        yield return StuffSpec();

        yield return FixedTypeSpec("ERROR_MESSAGE", new SqlType(SqlTypeCategory.NVarChar, Length: 4000), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_NUMBER", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_SEVERITY", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_STATE", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_LINE", new SqlType(SqlTypeCategory.Int), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("ERROR_PROCEDURE", new SqlType(SqlTypeCategory.NVarChar, Length: 128), HoleKind.EnvironmentDependent);
        yield return FixedTypeSpec("SCOPE_IDENTITY", new SqlType(SqlTypeCategory.Decimal, Precision: 38, Scale: 0), HoleKind.EnvironmentDependent);
    }

    private static BuiltinSpec DateAdd() => new(
        "DATEADD",
        Evaluate: null,
        HoleTransfer: call => BuiltinFoldResult.OkHole(
            call.Arguments is [_, _, BuiltinArgument.Hole { Type: { } thirdArgumentType }]
                ? TypeInference.BuiltinFunctionTypeResolver.ResolveDateAddResult(thirdArgumentType)
                : new SqlType(SqlTypeCategory.DateTime),
            call.Site,
            HoleKind.ArgumentIndependentReturnType),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static BuiltinSpec StuffSpec() => new(
        "STUFF",
        Evaluate: null,
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static BuiltinSpec EnvironmentNameSpec(string name) =>
        FixedTypeSpec(name, new SqlType(SqlTypeCategory.NVarChar, Length: 128), HoleKind.EnvironmentDependent);

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

    private static BuiltinFoldResult.Fail UnresolvedOrGeneric(IEnumerable<BuiltinArgument> arguments) =>
        arguments.OfType<BuiltinArgument.Unresolved>().FirstOrDefault() is { } unresolved
            ? new BuiltinFoldResult.Fail(unresolved.Reason)
            : new BuiltinFoldResult.Fail(SymbolicValueInFunctionArgument);

    private static BuiltinSpec TrimSpec(string name, Func<string, string> trim) => new(
        name,
        Evaluate: call => BuiltinFoldResult.OkText(trim(((BuiltinArgument.Text)call.Arguments[0]).Value), call.Site),
        HoleTransfer: PassThroughSingleArgumentType,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

    private static BuiltinFoldResult PassThroughSingleArgumentType(BuiltinCall call) => call.Arguments[0] switch
    {
        BuiltinArgument.Hole hole => BuiltinFoldResult.OkHole(hole.Type, call.Site, hole.Kind),
        BuiltinArgument.Unresolved { Type: { } declaredType } => BuiltinFoldResult.OkHole(declaredType, call.Site, HoleKind.ArgumentIndependentReturnType),
        _ => UnresolvedOrGeneric([call.Arguments[0]]),
    };

    private static BuiltinFoldResult PassThroughSourceType(BuiltinCall call, params ReadOnlySpan<BuiltinArgument> otherArguments) => call.Arguments[0] switch
    {
        BuiltinArgument.Hole hole => BuiltinFoldResult.OkHole(hole.Type, call.Site, hole.Kind),
        BuiltinArgument.Unresolved { Type: { } declaredType } => BuiltinFoldResult.OkHole(declaredType, call.Site, HoleKind.ArgumentIndependentReturnType),
        _ => UnresolvedOrGeneric([.. otherArguments, call.Arguments[0]]),
    };

    private static BuiltinSpec Left() => LeftOrRight("LEFT", (input, length) => input[..length]);

    private static BuiltinSpec Right() => LeftOrRight("RIGHT", (input, length) => input[^length..]);

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

        HoleTransfer: call => call.Arguments[1] is BuiltinArgument.Number { Value: < 0 }
            ? new BuiltinFoldResult.Fail("non-literal-expression:replicate-negative-count")
            : PassThroughSourceType(call, call.Arguments[1]),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

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

        HoleTransfer: call => call.Arguments[1] is BuiltinArgument.Number { Value: < 0 }
            ? new BuiltinFoldResult.Fail(NegativeLength)
            : PassThroughSourceType(call, call.Arguments[1]),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

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

    private static BuiltinSpec Replace() => new(
        "REPLACE",
        Evaluate: ReplaceEvaluate,
        HoleTransfer: ReplaceHoleTransfer,
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

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

        if (call.Arguments[0] is BuiltinArgument.Unresolved { Type: { } sourceDeclaredType })
        {
            return BuiltinFoldResult.OkHole(sourceDeclaredType, call.Site, HoleKind.ArgumentIndependentReturnType);
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

            return quoted is null
                ? new BuiltinFoldResult.Fail("non-literal-expression:quotename-null-result")
                : BuiltinFoldResult.OkText(quoted, call.Site);
        },

        HoleTransfer: call => QuoteNameHoleTransfer(call),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

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

    private static BuiltinSpec CharOrNChar(string name, int maxCodePoint, SqlTypeCategory category) => new(
        name,
        Evaluate: call =>
        {
            var codePoint = ((BuiltinArgument.Number)call.Arguments[0]).Value;
            return codePoint is < 0 || codePoint > maxCodePoint
                ? new BuiltinFoldResult.Fail("non-literal-expression:char-out-of-range")
                : BuiltinFoldResult.OkText(((char)codePoint).ToString(), call.Site);
        },

        HoleTransfer: call => BuiltinFoldResult.OkHole(new SqlType(category, Length: 1), call.Site, HoleKind.ArgumentIndependentReturnType),
        ReturnType: null,
        ReturnKind: default,
        UnconditionalFailReason: null);

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

    private static BuiltinSpec NonDeterministicTyped(string name, SqlType returnType) => FixedTypeSpec(name, returnType, HoleKind.NonDeterministicTyped);

    private static BuiltinSpec FixedTypeSpec(string name, SqlType returnType, HoleKind kind) => new(
        name, Evaluate: null, HoleTransfer: null, ReturnType: returnType, ReturnKind: kind, UnconditionalFailReason: null);
}

public sealed record BuiltinSpec(
    string Name,
    Func<BuiltinCall, BuiltinFoldResult>? Evaluate,
    Func<BuiltinCall, BuiltinFoldResult>? HoleTransfer,
    SqlType? ReturnType,
    HoleKind ReturnKind,
    string? UnconditionalFailReason);
