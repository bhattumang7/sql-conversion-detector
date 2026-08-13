namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// Why a <see cref="TemplatePiece.Hole"/> exists - a value this scanner could prove has a
/// known <see cref="Catalog.SqlType"/> but could not prove constant. Drives both rendering
/// (<see cref="TemplateRenderer"/>) and the decline-reason a site reports when a hole can never
/// resolve far enough to be typed at all (those cases produce <see cref="SqlTextValue.Tainted"/>
/// instead of a Hole - a Hole always carries a real type, never a guess).
/// </summary>
public enum HoleKind
{
    /// <summary>A formal parameter with no known caller passing a literal for it.</summary>
    UntypedParameter,

    /// <summary>A DECLARE with no initializer, or an explicit NULL initializer (treated as none).</summary>
    UninitializedDeclare,

    /// <summary>A builtin with a documented, fixed return type but a non-deterministic result (NEWID, GETDATE, RAND, CHECKSUM, ...).</summary>
    NonDeterministicTyped,

    /// <summary>A builtin/variable whose value depends on the server/session environment (SERVERPROPERTY, @@SERVERNAME, ...).</summary>
    EnvironmentDependent,

    /// <summary>Written by a statement this engine does not model precisely - the safe default (<see cref="Predicates.DynamicSqlValue"/> dataflow), never a taint, whenever the write target's declared type is known.</summary>
    HavocWrite,

    /// <summary>A <see cref="TemplatePiece.Choice"/> collapsed because its expansion would exceed the per-variable assembly cap.</summary>
    WidenedChoice,

    /// <summary>Stands for a whole optional clause/fragment, not a single scalar - renders as a single space rather than an identifier-shaped token, since no identifier could ever sit legally in that grammar position.</summary>
    OptionalFragment,

    /// <summary>A variable DECLAREd only inside a TRY block, referenced from its CATCH block - legal T-SQL (batch-wide storage exists regardless of whether the DECLARE line ever executed), but CATCH starts from the pre-TRY state, so how far TRY got before throwing is unknowable.</summary>
    TryOnlyDeclaration,

    /// <summary>A builtin whose return type is fixed by the T-SQL spec regardless of its own argument's value (QUOTENAME's nvarchar(258), STR's CHAR(n)) - used when that argument itself could not be resolved to a real value OR a typed hole (so there is no argument-derived <see cref="HoleKind"/> to propagate), yet the builtin's own return type is still a hard guarantee.</summary>
    ArgumentIndependentReturnType,

    /// <summary>A column reference inside a `SELECT @var = expr FROM &lt;single catalog-known table&gt;` assignment - the column's own catalog type is a hard fact, but which ROW's value it holds is data-dependent (CLAUDE.md: corpus DML never executes), so only the type transfers.</summary>
    RowDependentColumn,

    /// <summary>A call to a user-defined scalar function this scanner does not know how to evaluate, but whose RETURNS clause is a hard fact the catalog already read from its CREATE/ALTER FUNCTION DDL - the function's own body (and therefore its actual return VALUE) is never inspected, only its declared type.</summary>
    UserFunctionDeclaredReturnType,
}
