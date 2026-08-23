namespace SilentScan.Core.Predicates.DynamicSqlValue;

public enum HoleKind
{
    UntypedParameter,

    UninitializedDeclare,

    NonDeterministicTyped,

    EnvironmentDependent,

    HavocWrite,

    WidenedChoice,

    OptionalFragment,

    TryOnlyDeclaration,

    ArgumentIndependentReturnType,

    RowDependentColumn,

    UserFunctionDeclaredReturnType,
}
