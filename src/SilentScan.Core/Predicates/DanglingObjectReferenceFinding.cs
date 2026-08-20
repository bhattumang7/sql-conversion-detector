using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A stored procedure, view, function, or trigger names a table/view/synonym that the engine's
/// own binder cannot resolve to a real object right now - <c>CREATE</c>/<c>ALTER</c> succeeds
/// anyway (SQL Server defers name resolution for a module body until it actually runs), so the
/// module compiles clean and sits in the catalog looking correct until the first call that
/// reaches the missing reference, which fails with Msg 208 ("Invalid object name"). Oracle-
/// confirmed directly: a procedure referencing a table that was never created raises exactly that
/// error at <c>EXEC</c> time despite a clean <c>CREATE PROCEDURE</c>.
///
/// Live-mode only by construction, like <see cref="TempTableExecShapeFinding"/> and
/// <see cref="DatabaseConfigurationFinding"/> - there is no file-mode equivalent of "does the
/// engine's own binder resolve this name right now", since that answer depends on what actually
/// exists in the connected database's catalog, not on anything recoverable from DDL text alone.
///
/// <see cref="Line"/>/<see cref="Column"/> point at the module's own <c>CREATE</c>/<c>ALTER</c>
/// statement - the underlying catalog signal names which object is missing, not which statement
/// inside the module body reaches it, matching <see cref="ModuleCompileFlagFinding"/>'s identical
/// precedent for a module-granularity (not statement-granularity) live catalog fact.
/// </summary>
public sealed record DanglingObjectReferenceFinding(
    string ModuleQualifiedName,
    string ModuleTypeDescription,
    string ReferencedEntityName,
    string? ReferencedSchemaName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
