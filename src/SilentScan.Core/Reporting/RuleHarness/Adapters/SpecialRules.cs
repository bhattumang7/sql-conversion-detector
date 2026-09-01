using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class PartialCompositeForeignKeyJoinRule : IPerFileRule
{
    public string Id => "PartialCompositeForeignKeyJoinScanner";

    public object? Prepare(RuleContext context) => PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(context.Catalog);

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        PartialCompositeForeignKeyJoinScanner.Scan(parseResult, context.Catalog, (IReadOnlyList<PartialCompositeForeignKeyJoinScanner.CompositeForeignKey>)state!);

    public IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state)
    {
        var compositeForeignKeys = (IReadOnlyList<PartialCompositeForeignKeyJoinScanner.CompositeForeignKey>)state!;
        return compositeForeignKeys.Count == 0 ? null : PartialCompositeForeignKeyJoinScanner.CreateRule(parseResult.SourcePath, context.Catalog, compositeForeignKeys);
    }

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        PartialCompositeForeignKeyJoinScanner.Harvest((PartialCompositeForeignKeyJoinScanner.Rule)moduleRule);
}

internal sealed class ProcCallTableValuedArgumentMismatchRule : IPerFileRule
{
    public string Id => "ProcCallTableValuedArgumentMismatchScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        ProcCallTableValuedArgumentMismatchScanner.Scan(parseResult, context.ProcCallGraph, context.Catalog, context.Ledger);

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) =>
        ProcCallTableValuedArgumentMismatchScanner.CreateRule(parseResult.SourcePath, context.ProcCallGraph, context.Catalog, context.Ledger);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        ProcCallTableValuedArgumentMismatchScanner.Harvest((ProcCallTableValuedArgumentMismatchScanner.Rule)moduleRule);
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

    public IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state)
    {
        var candidates = (IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), TryCastComputedColumnPredicateScanner.Candidate>)state!;
        return candidates.Count == 0 ? null : TryCastComputedColumnPredicateScanner.CreateRule(parseResult.SourcePath, context.Catalog, candidates);
    }

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        TryCastComputedColumnPredicateScanner.Harvest((TryCastComputedColumnPredicateScanner.Rule)moduleRule);

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

internal sealed class StatementShapeRule : IPerFileRule
{
    public string Id => "StatementShapeScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => StatementShapeScanner.Scan(parseResult);

    public IReadOnlyList<IFinding> ScanCatalogOnce(RuleContext context) => StatementShapeScanner.ScanCatalog(context.Catalog);

    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<StatementShapeFinding>(f => f.Kind);

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => StatementShapeScanner.CreateRule(parseResult.SourcePath);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => StatementShapeScanner.Harvest((StatementShapeScanner.Rule)moduleRule);
}

internal sealed class MultiReferencedCteRule : IPerFileRule
{
    public string Id => "MultiReferencedCteScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => MultiReferencedCteScanner.Scan(parseResult, context.Catalog);

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => MultiReferencedCteScanner.CreateRule(parseResult.SourcePath, context.Catalog);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => MultiReferencedCteScanner.Harvest((MultiReferencedCteScanner.Rule)moduleRule);

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

internal sealed class RecursiveCteAnchorTypeMismatchRule : IPerFileRule
{
    public string Id => "RecursiveCteAnchorTypeMismatchScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        RecursiveCteAnchorTypeMismatchScanner.Scan(parseResult, context.Catalog, context.Lineage.AllRelations);

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) =>
        RecursiveCteAnchorTypeMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.Lineage.AllRelations);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        RecursiveCteAnchorTypeMismatchScanner.Harvest((RecursiveCteAnchorTypeMismatchScanner.Rule)moduleRule);

    public IComparer<IFinding>? Comparer => Comparer<IFinding>.Create((x, y) =>
    {
        var a = (RecursiveCteAnchorTypeMismatchFinding)x;
        var b = (RecursiveCteAnchorTypeMismatchFinding)y;
        var cmp = string.CompareOrdinal(a.SourcePath, b.SourcePath);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = a.Line.CompareTo(b.Line);
        return cmp != 0 ? cmp : a.Column.CompareTo(b.Column);
    });
}

internal sealed class PostExpansionJoinWidthRule : IPerFileRule
{
    public string Id => "PostExpansionJoinWidthScanner";

    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) =>
        PostExpansionJoinWidthScanner.Scan(parseResult, context.Catalog, context.ViewExpansionMap);

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) =>
        PostExpansionJoinWidthScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.ViewExpansionMap);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        PostExpansionJoinWidthScanner.Harvest((PostExpansionJoinWidthScanner.Rule)moduleRule);

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

    public IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) =>
        context.SelectStarViewCandidates.Count == 0
            ? null
            : SelectStarViewScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.Lineage, context.SelectStarViewCandidates);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        SelectStarViewScanner.Harvest((SelectStarViewScanner.Rule)moduleRule);

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

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => OutputParameterScanner.CreateRule(parseResult.SourcePath);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => OutputParameterScanner.Harvest((OutputParameterScanner.Rule)moduleRule);

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

    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => UnindexedTempTableUsageScanner.CreateRule(context.Catalog);

    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) =>
        UnindexedTempTableUsageScanner.Harvest(parseResult, context.Catalog, (UnindexedTempTableUsageScanner.Rule)moduleRule);

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
