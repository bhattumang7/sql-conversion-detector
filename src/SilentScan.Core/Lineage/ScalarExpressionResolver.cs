using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

/// <summary>Resolves a single SELECT-list scalar expression to its <see cref="ColumnProvenance"/>.</summary>
public static class ScalarExpressionResolver
{
    /// <summary>
    /// Bundles the context every recursive call in this class threads along - introduced so
    /// adding CAST/CONVERT's type-alias lookup (docs/audit-remediation-plan.md Phase 6.2)
    /// didn't push an already-4-parameter recursion past a sane parameter count.
    /// </summary>
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

        // Roadmap Phase B: arithmetic, CASE/COALESCE/NULLIF/IIF - previously always
        // InferredType: null via ResolveGenericExpression below, now typed through the shared
        // ExpressionTypeInferencer while STILL collecting every reachable column reference for
        // depth/indexed tracking, exactly as ResolveGenericExpression already did (a view column
        // built from `Price * Qty` needs both base columns' indexed-ness tracked, regardless of
        // whether the overall expression could be typed).
        ParenthesisExpression or UnaryExpression or BinaryExpression
            or CoalesceExpression or NullIfExpression or IIfCall
            or SearchedCaseExpression or SimpleCaseExpression =>
            ResolveTypedExpression(expression, context),

        // A built-in scalar function call (ISNULL/UPPER/LEFT/...) or a scalar UDF - previously
        // always InferredType: null here regardless of how trivially typeable the call was,
        // even though the IDENTICAL expression typed correctly when it appeared directly in a
        // predicate (TypedPredicateExtractor.ResolveFunctionCallOperand already consulted
        // BuiltinFunctionTypeResolver/the UDF registry). `CREATE VIEW v AS SELECT ISNULL(x,'')
        // AS c` left c permanently untyped, so every predicate through v against c went Unknown
        // even though the same WHERE ISNULL(x,'') = 'y' inline classified normally - the
        // asymmetry this method now closes.
        FunctionCall functionCall => ResolveFunctionCall(functionCall, context),

        _ => ResolveGenericExpression(expression, context),
    };

    /// <summary>Mirrors TypedPredicateExtractor.ResolveFunctionCallOperand's three-tier lookup (first-argument-type builtins, fixed-return-type builtins, then the scalar-UDF registry), reusing the exact same curated tables so the two passes can never disagree about what a given function types as. A function this scan never saw declared, or one this curated table doesn't cover, still resolves Unknown - never guessed.</summary>
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

        // Not a built-in - try the scalar UDF return-type registry, when a catalog is available
        // (the lineage pass always has one; a caller resolving an expression before a catalog
        // exists simply gets Unknown here, same as before this method existed).
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

        // CAST/CONVERT to a string-family type has no inline COLLATE syntax of its own (T-SQL
        // requires wrapping the whole CAST in a separate `... COLLATE ...` clause instead, a
        // distinct expression node this method never sees) - real SQL Server instead propagates
        // the INPUT expression's own collation into the result when the input is itself a
        // string, verified directly against the oracle (CAST(varcharCol AS NVARCHAR(n)) keeps
        // the source column's collation, not the database default). Without this, every
        // CAST-to-string column silently reported Collation=null regardless of what the real
        // engine does, understating how often a CAST-derived predicate's collation is actually
        // knowable - found by LineageParityCheckerTests diffing against a real deployed view.
        // A non-string input (CAST(intCol AS NVARCHAR(n))) correctly gets the database's
        // default collation in the real engine, which this pass has no reliable way to know
        // here - collation stays null (Unknown), not a guess, exactly as before.
        if (type.IsStringFamily && ColumnProvenanceAnalysis.TryGetScalarType(inner) is { IsStringFamily: true, Collation: { } innerCollation })
        {
            type = type with { Collation = innerCollation };
        }

        return new ColumnProvenance.Cast(type, inner, context.SourcePath, line);
    }

    /// <summary>
    /// Any scalar expression ScriptDOM hands us that isn't a plain column reference, CAST/
    /// CONVERT, or literal (a function call, arithmetic, CASE, ...). Rather than exhaustively
    /// modeling every ScriptDOM expression shape, this collects every column reference
    /// reachable anywhere inside the expression tree and resolves each one's own provenance -
    /// enough to tell whether a real, possibly-indexed base column sits underneath, without
    /// needing to mirror the expression's exact structure.
    /// </summary>
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
            // A wildcard reference (the `*` inside e.g. COUNT(*)) has no MultiPartIdentifier -
            // it isn't "a column" for lineage purposes, and it isn't sargability-relevant
            // either. Same class of bug as NonSargablePredicateScanner's earlier COUNT(*) fix.
            if (node.MultiPartIdentifier is { Identifiers.Count: > 0 })
            {
                References.Add(node);
            }
        }

        /// <summary>
        /// Stops descent into a nested subquery's own FROM/WHERE entirely - this pass has no
        /// outer-scope chain to resolve an inner-scope column against (unlike Pass 3's
        /// TypedPredicateExtractor, which threads a real ScopeStack), so a column reference
        /// strictly inside a subquery must never be collected as this outer expression's own
        /// input. Without this, <c>(SELECT SUM(Amount) FROM dbo.Payments)</c> nested inside a
        /// view's SELECT list had its inner Amount column resolved against the OUTER query's own
        /// FROM scope instead - a wrong base-column attribution whenever a same-named column
        /// happened to exist there, or spurious "not found in FROM scope" ledger noise otherwise.
        /// Converts the bug from "wrong attribution" to "honestly under-collected": the subquery's
        /// own scope genuinely isn't reachable here, so Unknown is the correct, conservative
        /// answer, not a deeper scope-chaining feature this pass was never given the plumbing for.
        /// </summary>
        public override void ExplicitVisit(ScalarSubquery node)
        {
            // Deliberately empty, no base call - stops descent so this collector never reaches
            // the subquery's own FROM/WHERE. See this method's own doc comment above.
        }
    }

    /// <summary>
    /// Resolves a column reference against a single FROM scope: the one algorithm both Pass 2
    /// (this class) and Pass 3 (<see cref="Predicates.TypedPredicateExtractor"/>) use, so a
    /// qualified reference whose qualifier doesn't resolve is unresolved everywhere, never
    /// silently falling back to a name-only search across the whole scope
    /// (docs/audit-remediation-plan.md Phase 2.1 - that fallback could bind a correlated
    /// outer-query reference like "o.Id" to an unrelated same-named column on a completely
    /// different table). Equivalent to the chain overload with a single-level chain.
    /// </summary>
    internal static ColumnProvenance ResolveColumnReference(
        ColumnReferenceExpression columnRef, IReadOnlyDictionary<string, ScopeEntry> scope, IReadOnlyList<ScopeEntry> orderedRelations, string sourcePath, SkipLedger? ledger) =>
        ResolveColumnReference(columnRef, [(scope, orderedRelations)], sourcePath, ledger);

    /// <summary>
    /// Resolves a column reference against a chain of nested FROM scopes, innermost first
    /// (docs/audit-remediation-plan.md Phase 2.2): a qualifier or unqualified column name is
    /// looked up in the innermost scope first, then progressively outer scopes, matching SQL's
    /// own correlated-subquery name resolution rule. An ambiguous match WITHIN one scope level
    /// stops the search there rather than skipping past it to an outer level - that ambiguity is
    /// real, not a reason to guess the query meant an enclosing query's column instead.
    /// </summary>
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

    /// <summary>
    /// Finds the NEAREST view/TVF a column reference resolves through, if any - the object
    /// literally named in the query's own FROM clause, as opposed to the chain overload of
    /// <c>ResolveColumnReference</c>'s fully-flattened <see cref="ColumnProvenance.BaseColumn"/>,
    /// which always names the ultimate physical table regardless of how many view layers sit
    /// between it and the predicate. Used by the Verify oracle (<c>CorpusFindingProbeBuilder</c>)
    /// to compile a probe against the SAME object the original predicate queried - a depth&gt;=1
    /// finding probed against the base table directly never actually exercises the view layer
    /// the finding claims the conversion is inherited through. Mirrors the same alias/unqualified
    /// matching rules as that chain overload exactly, but stops at the first scope-entry match
    /// instead of resolving all the way down,
    /// and only returns a result for a genuine view/TVF layer (a base table or CTE reference
    /// returns null - the base table IS already what TableQualifiedName names, nothing to route
    /// through differently). A qualified relation name here is not a promise it can be queried
    /// bare (an inline TVF needs call arguments this method has no way to reconstruct) - the
    /// caller is expected to treat a resulting compile failure as an honest ProbeFailed, not
    /// retry with a guess.
    /// </summary>
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
            // A UNION-view column's branches are each their own provenance chain (CLAUDE.md:
            // "record ALL branch types") - reading it through another view layer bumps every
            // branch, or depth silently stays 0 forever for any predicate against a union-
            // backed view read through further layers, skewing the study's depth histogram.
            ColumnProvenance.Union union => union with { Branches = [.. union.Branches.Select(b => BumpDepthIfViewLayer(b, isViewLayer))] },
            // Unknown has no depth to bump. The compiler's pattern-exhaustiveness check does
            // not treat this sealed-subtype set as closed (it still demands a catch-all even
            // with every concrete case listed), so this `_` can't be removed the way a real
            // closed-union language feature would allow - ColumnProvenanceSubtypeCoverageTests
            // is the actual forcing function: it reflects over every nested ColumnProvenance
            // subtype and fails if one appears here uncovered.
            _ => provenance,
        };
    }

    /// <summary>
    /// <c>col COLLATE X</c> where X genuinely differs from the column's own real collation
    /// compiles to an explicit <c>CONVERT</c> applied to the column itself (oracle-verified
    /// directly against Docker SQL Server: the plan shows <c>CONVERT(varchar(n), col, 0)</c>,
    /// not <c>CONVERT_IMPLICIT</c>) - structurally identical to a literal <c>CAST(col AS ...)</c>,
    /// so it reuses <see cref="ColumnProvenance.Cast"/> rather than inventing a parallel
    /// provenance shape; the rendered "CAST/CONVERT to ..." finding text is accurate to what the
    /// engine actually does, even though the source syntax was COLLATE, not CAST. When X
    /// matches the column's real collation, SQL Server elides the CONVERT entirely (also
    /// oracle-verified: a single clean Index Seek, no CONVERT anywhere in the plan) - a no-op,
    /// left unwrapped. When the column's real collation isn't resolvable at all, wrapping would
    /// assert a mismatch we can't prove, so the reference passes through unchanged (CLAUDE.md:
    /// never guess) rather than risk a false positive in either direction.
    /// </summary>
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
