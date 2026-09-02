using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

public sealed record ViewDefinition(
    string QualifiedName,
    SelectStatement SelectStatement,
    IReadOnlyList<string>? ExplicitColumnNames,
    string SourcePath,
    int SourceLine,
    bool WithCheckOption = false);
