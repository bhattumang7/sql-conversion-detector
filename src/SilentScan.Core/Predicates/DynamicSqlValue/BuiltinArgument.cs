using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public abstract record BuiltinArgument
{
    public sealed record Text(string Value) : BuiltinArgument;

    public sealed record Number(int Value) : BuiltinArgument;

    public sealed record Hole(SqlType Type, HoleKind Kind) : BuiltinArgument;

    public sealed record Unresolved(string Reason, SourceSpan Location, SqlType? Type = null) : BuiltinArgument;
}

public sealed record BuiltinCall(string FunctionName, IReadOnlyList<BuiltinArgument> Arguments, SourceSpan Site);

public abstract record BuiltinFoldResult
{
    public sealed record Ok(IReadOnlyList<TemplatePiece> Pieces) : BuiltinFoldResult;

    public sealed record Fail(string Reason) : BuiltinFoldResult;

    public static Ok OkText(string value, SourceSpan site) => new([new TemplatePiece.Lit(value, site, PrefixLength: 0)]);

    public static Ok OkHole(SqlType type, SourceSpan site, HoleKind kind) => new([new TemplatePiece.Hole(type, site, kind)]);
}
