namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep" - declared type of size 1 or 2
/// (<c>varchar(1)</c>, <c>nvarchar(2)</c>, ...). A narrow, DIFFERENT claim from the shipped
/// under-length-vs-compared-column stream (<see cref="UnderLengthParameterFinding"/>): this rule
/// needs no compared column at all - a string/binary declaration this small, on its own, is
/// almost always a truncated-from-a-larger-source mistake or a leftover single-character-flag
/// placeholder that later grew real string content. Purely advisory/structural (no oracle
/// applies - "this declaration looks like a mistake" is a code-smell judgment call, not a
/// provable runtime or plan-shape fact), <see cref="FindingConfidence.Low"/> by default, the
/// same no-magnitude-claim tier <see cref="LocalVariablePredicateFinding"/>/
/// <see cref="CascadingForeignKeyFinding"/> use for their own advisory reasons.
///
/// Covers two independent declaration sites, both DIRECT AST/catalog facts, no new machinery
/// shared between them: a real catalog COLUMN (<see cref="UndersizedDeclarationSite.TableColumn"/>)
/// and a DECLARE'd local variable or a procedure/function formal parameter
/// (<see cref="UndersizedDeclarationSite.Declaration"/>).
/// A temp table's/table variable's own column declarations inside a module body (via <c>CREATE
/// TABLE #temp</c>, <c>SELECT ... INTO #temp</c>, or <c>DECLARE @t TABLE(...)</c>) are covered
/// for free by the <see cref="UndersizedDeclarationSite.TableColumn"/> catalog scan - the
/// catalog already registers all three under <c>DatabaseCatalog.Tables</c> for other, earlier
/// consumers (confirmed directly: real findings against the local test database's own temp
/// tables), not a separate pass this stream had to build.
/// </summary>
public enum UndersizedDeclarationSite
{
    TableColumn,
    Declaration,
}

public sealed record UndersizedDeclarationFinding(
    UndersizedDeclarationSite Site,
    string QualifiedOrVariableName,
    string TypeDescription,
    int Length,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.Low);
