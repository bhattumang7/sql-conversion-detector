namespace SilentScan.Core.Predicates;

public sealed record ProcedureOutputSummary(string QualifiedName, string ParameterName, IReadOnlyList<string> PossibleValues);
