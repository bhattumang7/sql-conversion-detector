using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public sealed record GuardedAlternative(string GuardText, SqlTextValue.Template Value);

public abstract record SqlTextValue
{
    public SqlType? DeclaredType { get; init; }

    public IReadOnlyList<GuardedAlternative>? GuardedAlternatives { get; init; }

    public sealed record Template(IReadOnlyList<TemplatePiece> Pieces) : SqlTextValue;

    public sealed record Tainted(string Reason, SourceSpan Location) : SqlTextValue;

    public const string CardinalityCapReason = "diverges-across-if-branches:cardinality-cap";

    public const string DivergesInControlFlowGraphReason = "diverges-in-control-flow-graph";

    public static SqlTextValue Concat(SqlTextValue a, SqlTextValue b)
    {
        if (a is Tainted taintedA)
        {
            if (taintedA.GuardedAlternatives is { Count: > 0 } alternatives && b is Template)
            {

                var extended = alternatives.Select(alt => alt with { Value = WithoutOwnAlternatives((Template)Concat(alt.Value, b)) }).ToList();
                return taintedA with { GuardedAlternatives = extended };
            }

            if (b is Template { Pieces.Count: > 0 } bTemplateForHole && taintedA.DeclaredType is { } typeA)
            {
                return new Template([new TemplatePiece.Hole(typeA, taintedA.Location, HoleKind.HavocWrite), .. bTemplateForHole.Pieces]);
            }

            return a;
        }

        if (b is Tainted taintedB)
        {
            if (taintedB.GuardedAlternatives is { Count: > 0 } alternativesB && a is Template)
            {

                var extended = alternativesB.Select(alt => alt with { Value = WithoutOwnAlternatives((Template)Concat(a, alt.Value)) }).ToList();
                return taintedB with { GuardedAlternatives = extended };
            }

            if (a is Template { Pieces.Count: > 0 } aTemplateForHole && taintedB.DeclaredType is { } typeB)
            {
                return new Template([.. aTemplateForHole.Pieces, new TemplatePiece.Hole(typeB, taintedB.Location, HoleKind.HavocWrite)]);
            }

            return b;
        }

        var aTemplate = (Template)a;
        var bTemplate = (Template)b;
        var result = new Template([.. aTemplate.Pieces, .. bTemplate.Pieces]);
        return PropagateGuardedAlternativesThroughConcat(result, aTemplate, bTemplate);
    }

    private static Template PropagateGuardedAlternativesThroughConcat(Template result, Template a, Template b)
    {
        var combined = (SqlTextValue)result;
        if (a.GuardedAlternatives is { Count: > 0 } aAlternatives)
        {
            foreach (var alt in aAlternatives)
            {
                combined = WithGuardedAlternative(combined, alt.GuardText, (Template)Concat(alt.Value, b));
            }
        }

        if (b.GuardedAlternatives is { Count: > 0 } bAlternatives)
        {
            foreach (var alt in bAlternatives)
            {
                combined = WithGuardedAlternative(combined, alt.GuardText, (Template)Concat(a, alt.Value));
            }
        }

        return (Template)combined;
    }

    public const int MaxGuardedAlternatives = 8;

    public static SqlTextValue WithGuardedAlternative(SqlTextValue value, string guardText, Template branchValue)
    {
        var existing = value.GuardedAlternatives ?? [];
        var combined = existing.Where(a => !string.Equals(a.GuardText, guardText, StringComparison.Ordinal)).ToList();
        combined.Add(new GuardedAlternative(guardText, WithoutOwnAlternatives(branchValue)));
        if (combined.Count > MaxGuardedAlternatives)
        {
            combined.RemoveAt(0);
        }

        return value switch
        {
            Template t => t with { GuardedAlternatives = combined },
            Tainted t => t with { GuardedAlternatives = combined },
            _ => value,
        };
    }

    private static Tainted MergeAlternatives(Tainted a, Tainted b)
    {
        var merged = (SqlTextValue)a;
        var ownGuards = new HashSet<string>((a.GuardedAlternatives ?? []).Select(alt => alt.GuardText), StringComparer.Ordinal);
        foreach (var alt in (b.GuardedAlternatives ?? []).Where(alt => ownGuards.Add(alt.GuardText)))
        {
            merged = WithGuardedAlternative(merged, alt.GuardText, alt.Value);
        }

        return (Tainted)merged;
    }

    private static Template WithoutOwnAlternatives(Template template) =>
        template.GuardedAlternatives is { Count: > 0 } ? template with { GuardedAlternatives = null } : template;

    public static SqlTextValue Join(SqlTextValue a, SqlTextValue b, string guardText, int cap, SourceSpan at)
    {
        if (StructurallyEqual(a, b))
        {
            return a;
        }

        if (a is Template aTemplate && b is Template bTemplate)
        {
            var declaredType = a.DeclaredType ?? b.DeclaredType;

            if (aTemplate.Pieces is [TemplatePiece.Hole { } aHole] && bTemplate.Pieces is [TemplatePiece.Hole { } bHole] && aHole.Type == bHole.Type)
            {
                return aTemplate with { DeclaredType = declaredType };
            }

            var merged = MergeAsChoice(aTemplate, bTemplate, guardText);
            return Widen(merged with { DeclaredType = declaredType }, cap, at);
        }

        var uniform = TryGetUniformType(a, b);
        var carriedType = a.DeclaredType ?? b.DeclaredType;
        if (uniform is { } type)
        {
            return new Template([new TemplatePiece.Hole(type, at, HoleKind.WidenedChoice)]) { DeclaredType = carriedType };
        }

        return (a, b) switch
        {
            (Tainted onlyA, Template bOnly) => WithGuardedAlternative(onlyA with { DeclaredType = carriedType }, guardText, bOnly),
            (Template aOnly, Tainted onlyB) => WithGuardedAlternative(onlyB with { DeclaredType = carriedType }, guardText, aOnly),
            (Tainted bothTaintedA, Tainted bothTaintedB) => MergeAlternatives(bothTaintedA with { DeclaredType = carriedType }, bothTaintedB),
            _ => new Tainted(DivergesInControlFlowGraphReason, at) { DeclaredType = carriedType },
        };
    }

    private static Template MergeAsChoice(Template a, Template b, string guardText)
    {
        var aAlternatives = AsChoiceAlternatives(a, guardText);
        var bAlternatives = AsChoiceAlternatives(b, guardText);

        if (aAlternatives is null && bAlternatives is null)
        {
            return new Template([new TemplatePiece.Choice(guardText, [a, b])]);
        }

        var combined = new List<Template>(aAlternatives ?? [a]);
        combined.AddRange((bAlternatives ?? [b]).Where(alt => !combined.Any(existing => StructurallyEqual(existing, alt))));

        return new Template([new TemplatePiece.Choice(guardText, combined)]);
    }

    private static IReadOnlyList<Template>? AsChoiceAlternatives(Template value, string guardText) =>
        value.Pieces is [TemplatePiece.Choice { GuardText: var g } choice] && string.Equals(g, guardText, StringComparison.Ordinal)
            ? choice.Alternatives
            : null;

    public static bool StructurallyEqual(SqlTextValue a, SqlTextValue b) => (a, b) switch
    {
        (Tainted x, Tainted y) => x.Reason == y.Reason && x.Location.Equals(y.Location) && x.DeclaredType == y.DeclaredType && GuardedAlternativesEqual(x.GuardedAlternatives, y.GuardedAlternatives),
        (Template x, Template y) => x.DeclaredType == y.DeclaredType && PiecesEqual(x.Pieces, y.Pieces) && GuardedAlternativesEqual(x.GuardedAlternatives, y.GuardedAlternatives),
        _ => false,
    };

    private static bool GuardedAlternativesEqual(IReadOnlyList<GuardedAlternative>? a, IReadOnlyList<GuardedAlternative>? b)
    {
        if (a is null or { Count: 0 } && b is null or { Count: 0 })
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        var byGuardTextA = a.ToDictionary(alt => alt.GuardText, alt => alt.Value, StringComparer.Ordinal);
        return b.All(altB => byGuardTextA.TryGetValue(altB.GuardText, out var valueA) && StructurallyEqual(valueA, altB.Value));
    }

    private static bool PiecesEqual(IReadOnlyList<TemplatePiece> a, IReadOnlyList<TemplatePiece> b) =>
        a.Count == b.Count && a.Zip(b, PieceEqual).All(equal => equal);

    private static bool PieceEqual(TemplatePiece a, TemplatePiece b) => (a, b) switch
    {
        (TemplatePiece.Lit x, TemplatePiece.Lit y) => x == y,
        (TemplatePiece.Hole x, TemplatePiece.Hole y) => x == y,
        (TemplatePiece.Choice x, TemplatePiece.Choice y) => x.GuardText == y.GuardText && AlternativesEqual(x.Alternatives, y.Alternatives),
        _ => false,
    };

    private static bool AlternativesEqual(IReadOnlyList<Template> a, IReadOnlyList<Template> b) =>
        a.Count == b.Count && a.Zip(b, StructurallyEqual).All(equal => equal);

    public static SqlTextValue Widen(SqlTextValue value, int cap, SourceSpan at)
    {
        if (value is Tainted)
        {
            return value;
        }

        var template = (Template)value;
        var newPieces = new List<TemplatePiece>(template.Pieces.Count);
        foreach (var piece in template.Pieces)
        {
            if (piece is not TemplatePiece.Choice choice)
            {
                newPieces.Add(piece);
                continue;
            }

            var widenedAlternatives = new List<Template>(choice.Alternatives.Count);
            foreach (var alternative in choice.Alternatives)
            {
                var widenedAlternative = Widen(alternative, cap, at);
                if (widenedAlternative is Tainted)
                {
                    return new Tainted(CardinalityCapReason, at) { DeclaredType = value.DeclaredType };
                }

                widenedAlternatives.Add((Template)widenedAlternative);
            }

            newPieces.Add(choice with { Alternatives = widenedAlternatives });
        }

        var widened = new Template(newPieces) { DeclaredType = value.DeclaredType };
        if (ExpansionCount(widened) <= cap)
        {
            return widened;
        }

        var uniform = value.DeclaredType ?? TryGetUniformType(newPieces.OfType<TemplatePiece.Choice>().SelectMany(c => c.Alternatives));
        return uniform is { } type
            ? new Template([new TemplatePiece.Hole(type, at, HoleKind.WidenedChoice)]) { DeclaredType = value.DeclaredType }
            : new Tainted(CardinalityCapReason, at) { DeclaredType = value.DeclaredType };
    }

    public const string ExpansionSizeCapReason = "expanded-assembly-size-cap";

    public const long MaxExpandedPieceTotal = 1L << 20;

    public static long ExpandedPieceTotal(Template template)
    {
        long count = 1;
        long total = 0;
        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                long alternativeCountSum = 0;
                long alternativeTotalSum = 0;
                foreach (var alternative in choice.Alternatives)
                {
                    alternativeCountSum += ExpansionCount(alternative);
                    alternativeTotalSum += ExpandedPieceTotal(alternative);
                }

                total = total * alternativeCountSum + count * alternativeTotalSum;
                count *= alternativeCountSum;
            }
            else
            {
                total += count;
            }
        }

        return total;
    }

    public static long ExpansionCount(Template template)
    {
        long count = 1;
        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                count *= choice.Alternatives.Sum(ExpansionCount);
            }
        }

        return count;
    }

    public static IReadOnlyList<IReadOnlyList<FlatPiece>> Expand(Template template, int maxAssemblies)
    {
        var assemblies = new List<List<FlatPiece>> { new() };

        foreach (var piece in template.Pieces)
        {
            if (piece is TemplatePiece.Choice choice)
            {
                assemblies = ForkAssemblies(assemblies, choice, maxAssemblies);
            }
            else
            {
                var flat = FlatPiece.From(piece);
                foreach (var assembly in assemblies)
                {
                    assembly.Add(flat);
                }
            }
        }

        if (assemblies.Count > maxAssemblies)
        {
            throw new InvalidOperationException(
                $"Expand produced {assemblies.Count} assemblies, exceeding the {maxAssemblies} cap - Widen should have collapsed this Choice before Expand ever ran.");
        }

        return assemblies;
    }

    private static List<List<FlatPiece>> ForkAssemblies(List<List<FlatPiece>> assemblies, TemplatePiece.Choice choice, int maxAssemblies)
    {
        var forked = new List<List<FlatPiece>>();
        foreach (var prefix in assemblies)
        {
            foreach (var alternative in choice.Alternatives)
            {
                foreach (var alternativeAssembly in Expand(alternative, maxAssemblies))
                {
                    var combined = new List<FlatPiece>(prefix.Count + alternativeAssembly.Count);
                    combined.AddRange(prefix);
                    combined.AddRange(alternativeAssembly);
                    forked.Add(combined);
                }
            }
        }

        return forked;
    }

    public static bool ContainsHole(IReadOnlyList<FlatPiece> assembly) => assembly.Any(p => p is FlatPiece.Hole);

    public static SqlType? TryGetUniformType(SqlTextValue a, SqlTextValue b) => TryGetUniformType([a, b]);

    public static SqlType? TryGetUniformType(IEnumerable<SqlTextValue> values)
    {
        SqlType? uniform = null;
        var any = false;
        foreach (var value in values)
        {
            any = true;
            if (value.DeclaredType is not { } type)
            {
                return null;
            }

            if (uniform is null)
            {
                uniform = type;
            }
            else if (uniform != type)
            {
                return null;
            }
        }

        return any ? uniform : null;
    }
}
