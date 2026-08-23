using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

public static class ScalarExpressionResolver
{
internal readonly record struct ExpressionContext(
        IReadOnlyDictionary<string, ScopeEntry> Scope,
        IReadOnlyList<ScopeEntry> OrderedRelations,
        string SourcePath,
        SkipLedger? Ledger,
        IReadOnlyDictionary<string, SqlType>? TypeAliases,
        DatabaseCatalog? Catalog = null);

    public static ColumnProvenance Resolve(
        ScalarExpression expression,
        IReadOnlyDictionary<string, ScopeEntry> scope,
        IReadOnlyList<ScopeEntry> orderedRelations,
        string sourcePath,
        SkipLedger? ledger = null,
        IReadOnlyDictionary<string, SqlType>? typeAliases = null,
        DatabaseCatalog? catalog = null) =>
        Resolve(expression, new ExpressionContext(scope, orderedRelations, sourcePath, ledger, typeAliases, catalog));

    private static ColumnProvenance Resolve(ScalarExpression expression, ExpressionContext context) => expression switch
    {
        ColumnReferenceExpression columnRef => ResolveColumnReference(columnRef, context.Scope, context.OrderedRelations, context.SourcePath, context.Ledger),
        CastCall castCall => ResolveCastOrConvert(castCall.DataType, castCall.Parameter, context, castCall.StartLine),
        ConvertCall convertCall => ResolveCastOrConvert(convertCall.DataType, convertCall.Parameter, context, convertCall.StartLine),
        Literal literal => new ColumnProvenance.Expression(LiteralTypeResolver.Resolve(literal), Inputs: []),

        ParenthesisExpression or UnaryExpression or BinaryExpression
            or CoalesceExpression or NullIfExpression or IIfCall
            or SearchedCaseExpression or SimpleCaseExpression =>
            ResolveTypedExpression(expression, context),

        FunctionCall functionCall => ResolveFunctionCall(functionCall, context),

        _ => ResolveGenericExpression(expression, context),
    };

private static ColumnProvenance.Expression ResolveFunctionCall(FunctionCall functionCall, ExpressionContext context)
    {
        var inputs = CollectColumnInputs(functionCall, context);
        var name = functionCall.FunctionName.Value;

        if (BuiltinFunctionTypeResolver.TryGetArgumentTypeIndex(name) is { } argumentIndex && functionCall.Parameters.Count > argumentIndex)
        {
            var argumentType = ColumnProvenanceAnalysis.TryGetScalarType(Resolve(functionCall.Parameters[argumentIndex], context));
            argumentType = BuiltinFunctionTypeResolver.AdjustArgumentTypeFunctionResult(name, argumentType);
            return new ColumnProvenance.Expression(argumentType, inputs, context.SourcePath, functionCall.StartLine);
        }

        var fixedType = BuiltinFunctionTypeResolver.ResolveFixedReturnType(name);
        if (fixedType is not null)
        {
            return new ColumnProvenance.Expression(fixedType, inputs, context.SourcePath, functionCall.StartLine);
        }

        if (context.Catalog is { } catalog)
        {
            var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(functionCall);
            if (catalog.TryGetScalarFunctionReturnType(qualifiedName, out var udfType))
            {
                return new ColumnProvenance.Expression(udfType, inputs, context.SourcePath, functionCall.StartLine);
            }
        }

        return new ColumnProvenance.Expression(InferredType: null, inputs, context.SourcePath, functionCall.StartLine);
    }

    private static ColumnProvenance.Expression ResolveTypedExpression(ScalarExpression expression, ExpressionContext context)
    {
        var inputs = CollectColumnInputs(expression, context);
        var inferredType = ExpressionTypeInferencer.Resolve(
            expression, sub => ColumnProvenanceAnalysis.TryGetScalarType(Resolve(sub, context)), context.TypeAliases);
        return new ColumnProvenance.Expression(inferredType, inputs, context.SourcePath, expression.StartLine);
    }

    private static ColumnProvenance ResolveCastOrConvert(DataTypeReference dataType, ScalarExpression parameter, ExpressionContext context, int line)
    {
        var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, context.TypeAliases);
        if (resolved is not { } type)
        {
            context.Ledger?.Record(AnalysisPass.Lineage, context.SourcePath, line, dataType.StartColumn, "CAST/CONVERT", "target type could not be resolved");
            return new ColumnProvenance.Unknown("CAST/CONVERT target type could not be resolved");
        }

        var inner = Resolve(parameter, context);

        if (type.IsStringFamily && ColumnProvenanceAnalysis.TryGetScalarType(inner) is { IsStringFamily: true, Collation: { } innerCollation })
        {
            type = type with { Collation = innerCollation };
        }

        return new ColumnProvenance.Cast(type, inner, context.SourcePath, line);
    }

private static ColumnProvenance.Expression ResolveGenericExpression(ScalarExpression expression, ExpressionContext context) =>
        new(InferredType: null, CollectColumnInputs(expression, context), context.SourcePath, expression.StartLine);

    private static List<ColumnProvenance> CollectColumnInputs(ScalarExpression expression, ExpressionContext context)
    {
        var collector = new ColumnReferenceCollector();
        expression.Accept(collector);
        return collector.References.Select(columnRef => ResolveColumnReference(columnRef, context.Scope, context.OrderedRelations, context.SourcePath, context.Ledger)).ToList();
    }

    private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> References { get; } = [];

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is { Identifiers.Count: > 0 })
            {
                References.Add(node);
            }
        }

public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

internal static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, SkipLedger? ledger) =>
        ResolveColumnReference(columnRef, [(scope, orderedRelations)], sourcePath, ledger);

internal static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        string sourcePath,
        SkipLedger? ledger)
    {
        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        var columnName = identifiers[^1].Value;

        ColumnProvenance.Unknown Unresolved(string reason)
        {
            ledger?.Record(AnalysisPass.Lineage, sourcePath, columnRef.StartLine, columnRef.StartColumn, "column reference", reason);
            return new ColumnProvenance.Unknown(reason);
        }

        if (identifiers.Count >= 2)
        {
            var qualifier = identifiers[^2].Value;
            foreach (var (byAlias, _) in scopeChain)
            {
                if (!byAlias.TryGetValue(qualifier, out var entry))
                {
                    continue;
                }

                var column = entry.Relation.FindColumn(columnName);
                return column is null
                    ? Unresolved($"column '{columnName}' not found on '{qualifier}'")
                    : ApplyExplicitCollate(columnRef, BumpDepthIfViewLayer(column.Provenance, entry.IsViewLayer), sourcePath);
            }

            return Unresolved($"unknown table alias '{qualifier}'");
        }

        foreach (var (_, ordered) in scopeChain)
        {
            var matches = ordered
                .Select(entry => (Entry: entry, Column: entry.Relation.FindColumn(columnName)))
                .Where(m => m.Column is not null)
                .ToList();

            if (matches.Count == 1)
            {
                return ApplyExplicitCollate(columnRef, BumpDepthIfViewLayer(matches[0].Column!.Provenance, matches[0].Entry.IsViewLayer), sourcePath);
            }

            if (matches.Count > 1)
            {
                return Unresolved($"column '{columnName}' is ambiguous across the FROM scope");
            }
        }

        return Unresolved($"column '{columnName}' not found in FROM scope");
    }

internal static (string RelationQualifiedName, string ExposedColumnName)? TryResolveImmediateRelation(
        ColumnReferenceExpression columnRef,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
    {
        var identifiers = columnRef.MultiPartIdentifier.Identifiers;
        var columnName = identifiers[^1].Value;

        if (identifiers.Count >= 2)
        {
            var qualifier = identifiers[^2].Value;
            foreach (var (byAlias, _) in scopeChain)
            {
                if (!byAlias.TryGetValue(qualifier, out var entry))
                {
                    continue;
                }

                var column = entry.Relation.FindColumn(columnName);
                return column is not null && entry.IsViewLayer && entry.Relation.QualifiedName is { } qualifiedName
                    ? (qualifiedName, column.Name)
                    : null;
            }

            return null;
        }

        foreach (var (_, ordered) in scopeChain)
        {
            var matches = ordered
                .Select(entry => (Entry: entry, Column: entry.Relation.FindColumn(columnName)))
                .Where(m => m.Column is not null)
                .ToList();

            if (matches.Count == 1)
            {
                var (entry, column) = matches[0];
                return entry.IsViewLayer && entry.Relation.QualifiedName is { } qualifiedName
                    ? (qualifiedName, column!.Name)
                    : null;
            }

            if (matches.Count > 0)
            {
                return null;
            }
        }

        return null;
    }

    internal static ColumnProvenance BumpDepthIfViewLayer(ColumnProvenance provenance, bool isViewLayer)
    {
        if (!isViewLayer)
        {
            return provenance;
        }

        return provenance switch
        {
            ColumnProvenance.BaseColumn bc => bc with { Depth = bc.Depth + 1 },
            ColumnProvenance.Cast cast => cast with { Depth = cast.Depth + 1, Inner = BumpDepthIfViewLayer(cast.Inner, isViewLayer) },
            ColumnProvenance.Expression expr => expr with { Depth = expr.Depth + 1, Inputs = [.. expr.Inputs.Select(i => BumpDepthIfViewLayer(i, isViewLayer))] },
            ColumnProvenance.Declared declared => declared with { Depth = declared.Depth + 1 },
            ColumnProvenance.Union union => union with { Branches = [.. union.Branches.Select(b => BumpDepthIfViewLayer(b, isViewLayer))] },
            _ => provenance,
        };
    }

private static ColumnProvenance ApplyExplicitCollate(ColumnReferenceExpression columnRef, ColumnProvenance provenance, string sourcePath)
    {
        if (columnRef.Collation is not { Value: { } explicitCollationName })
        {
            return provenance;
        }

        if (ColumnProvenanceAnalysis.TryGetScalarType(provenance) is not { IsStringFamily: true, Collation: { } realCollation } type
            || string.Equals(explicitCollationName, realCollation.Name, StringComparison.OrdinalIgnoreCase))
        {
            return provenance;
        }

        var recollatedType = type with { Collation = new Collation(explicitCollationName) };
        return new ColumnProvenance.Cast(recollatedType, provenance, sourcePath, columnRef.StartLine);
    }
}
