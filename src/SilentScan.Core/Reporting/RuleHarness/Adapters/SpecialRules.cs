using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class PartialCompositeForeignKeyJoinRule : IPerFileRule
{
    public string Id => "PartialCompositeForeignKeyJoinScanner";

    public object? Prepare(RuleContext context) => PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(context.Catalog);

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        PartialCompositeForeignKeyJoinScanner.Scan(parseResult, context.Catalog, (IReadOnlyList<PartialCompositeForeignKeyJoinScanner.CompositeForeignKey>)state!);
}

internal sealed class TryCastComputedColumnPredicateRule : IPerFileRule
{
    public string Id => "TryCastComputedColumnPredicateScanner";

    public object? Prepare(RuleContext context) => TryCastComputedColumnPredicateScanner.BuildCandidates(context.Catalog);

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        TryCastComputedColumnPredicateScanner.Scan(
            parseResult,
            context.Catalog,
            (IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), TryCastComputedColumnPredicateScanner.Candidate>)state!);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (TryCastComputedColumnPredicateFinding)x;
        var b = (TryCastComputedColumnPredicateFinding)y;
        var cmp = string.CompareOrdinal(a.TableQualifiedName, b.TableQualifiedName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = string.CompareOrdinal(a.ColumnName, b.ColumnName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = string.CompareOrdinal(a.Location.SourcePath, b.Location.SourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.Location.Line.CompareTo(b.Location.Line);
        return cmp != 0 ? cmp : a.Location.Column.CompareTo(b.Location.Column);
    });
}

internal sealed class UndersizedDeclarationRule : IPerFileRule
{
    public string Id => "UndersizedDeclarationScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        UndersizedDeclarationScanner.ScanDeclarations(parseResult, context.Catalog);

    public IReadOnlyList<IFinding> ScanCatalogOnce(RuleContext context) => UndersizedDeclarationScanner.ScanCatalog(context.Catalog);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (UndersizedDeclarationFinding)x;
        var b = (UndersizedDeclarationFinding)y;
        var cmp = string.CompareOrdinal(a.QualifiedOrVariableName, b.QualifiedOrVariableName);
        return cmp != 0 ? cmp : DefaultLocationComparer.Instance.Compare(x, y);
    });
}

internal sealed class StatementShapeRule : IPerFileRule
{
    public string Id => "StatementShapeScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => StatementShapeScanner.Scan(parseResult);

    public IReadOnlyList<IFinding> ScanCatalogOnce(RuleContext context) => StatementShapeScanner.ScanCatalog(context.Catalog);

    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<StatementShapeFinding>(f => f.Kind);
}

internal sealed class MultiReferencedCteRule : IPerFileRule
{
    public string Id => "MultiReferencedCteScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => MultiReferencedCteScanner.Scan(parseResult);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (MultiReferencedCteFinding)x;
        var b = (MultiReferencedCteFinding)y;
        var cmp = string.CompareOrdinal(a.SourcePath, b.SourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.Line.CompareTo(b.Line);
        return cmp != 0 ? cmp : string.CompareOrdinal(a.CteName, b.CteName);
    });
}

internal sealed class PostExpansionJoinWidthRule : IPerFileRule
{
    public string Id => "PostExpansionJoinWidthScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        PostExpansionJoinWidthScanner.Scan(parseResult, context.Catalog, context.ViewExpansionMap);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (PostExpansionJoinWidthFinding)x;
        var b = (PostExpansionJoinWidthFinding)y;
        var cmp = (b.ExpandedCount - b.WrittenCount).CompareTo(a.ExpandedCount - a.WrittenCount);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = DefaultLocationComparer.Instance.Compare(x, y);
        return cmp != 0 ? cmp : string.CompareOrdinal(a.ModuleQualifiedName, b.ModuleQualifiedName);
    });
}

internal sealed class SelectStarViewRule : IPerFileRule
{
    public string Id => "SelectStarViewScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        SelectStarViewScanner.Scan(parseResult, context.Catalog, context.Lineage, context.SelectStarViewCandidates);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (SelectStarViewFinding)x;
        var b = (SelectStarViewFinding)y;
        var cmp = string.CompareOrdinal(a.ViewQualifiedName, b.ViewQualifiedName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = string.CompareOrdinal(a.ConsumerSourcePath, b.ConsumerSourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.ConsumerLine.CompareTo(b.ConsumerLine);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.ConsumerColumn.CompareTo(b.ConsumerColumn);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.ViewDepth.CompareTo(b.ViewDepth);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = string.CompareOrdinal(a.ViewSourcePath, b.ViewSourcePath);
        return cmp != 0 ? cmp : a.ViewLine.CompareTo(b.ViewLine);
    });
}

internal sealed class OutputParameterRule : IPerFileRule
{
    public string Id => "OutputParameterScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => OutputParameterScanner.Scan(parseResult);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (OutputParameterFinding)x;
        var b = (OutputParameterFinding)y;
        var cmp = string.CompareOrdinal(a.SourcePath, b.SourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.ProcedureLine.CompareTo(b.ProcedureLine);
        return cmp != 0 ? cmp : string.Compare(a.ParameterName, b.ParameterName, StringComparison.OrdinalIgnoreCase);
    });
}

internal sealed class UnindexedTempTableUsageRule : IPerFileRule
{
    public string Id => "UnindexedTempTableUsageScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => UnindexedTempTableUsageScanner.Scan(parseResult, context.Catalog);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (UnindexedTempTableUsageFinding)x;
        var b = (UnindexedTempTableUsageFinding)y;
        var cmp = string.CompareOrdinal(a.SourcePath, b.SourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.DeclarationLine.CompareTo(b.DeclarationLine);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = string.CompareOrdinal(a.TempTableQualifiedName, b.TempTableQualifiedName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.UsageLine.CompareTo(b.UsageLine);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.UsageColumn.CompareTo(b.UsageColumn);
        return cmp != 0 ? cmp : a.Kind.CompareTo(b.Kind);
    });
}
