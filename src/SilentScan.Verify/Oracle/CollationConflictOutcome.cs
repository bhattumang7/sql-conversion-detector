namespace SilentScan.Verify.Oracle;

/// <summary>Result of oracle-probing a single <see cref="Core.Predicates.CollationConflictFinding"/> against the real engine.</summary>
public enum CollationConflictOutcome
{
    /// <summary>The probe failed to compile with SQL Server error 468 ("Cannot resolve the collation conflict"), exactly as the finding claims.</summary>
    Confirmed,

    /// <summary>The probe compiled cleanly - the finding's claim that this comparison does not compile was wrong.</summary>
    NotConfirmed,

    /// <summary>The probe failed to compile for a DIFFERENT reason than a collation conflict (e.g. the column no longer matches the deployed DDL) - neither confirms nor refutes the finding.</summary>
    ProbeFailed,
}
