namespace SilentScan.Core.Predicates;

/// <summary>
/// One procedure's own OUTPUT-declared formal parameter, and the possible constant string
/// values this scan proved it always assigns before returning - the value(s) a caller doing
/// <c>EXEC dbo.SomeProc @out = @var OUTPUT</c> can trust <c>@var</c> to hold afterward, feeding
/// forward into that caller's own dynamic-SQL constant folding the same way a plain input
/// parameter's literal argument already does via <see cref="ProcCallGraph"/>
/// (<see cref="DynamicSqlScanner"/>'s "seed only when provably constant, taint otherwise" rule
/// applies here identically). Computed straight from the SAME fold state
/// <see cref="DynamicSqlScanner"/> already builds while walking a procedure body for its own
/// EXEC sites - an OUTPUT parameter is just an ordinary local variable inside that body, so
/// whatever <see cref="DynamicSqlScanner"/> proved it holds at the end of the body IS the value
/// returned. There is no entry at all for a parameter this scan could not prove foldable -
/// never a partial or best-guess value.
/// </summary>
public sealed record ProcedureOutputSummary(string QualifiedName, string ParameterName, IReadOnlyList<string> PossibleValues);
