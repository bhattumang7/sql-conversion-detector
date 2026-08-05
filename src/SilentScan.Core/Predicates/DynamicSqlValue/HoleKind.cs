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
}
