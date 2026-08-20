using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3 Tier-1: syntactic non-sargable predicate detection that needs no type/lineage
/// information (CLAUDE.md: "Tier-1 syntactic rules (no types needed)"). Scoped to comparison
/// and LIKE predicates specifically inside a genuine filter context - WHERE, a JOIN's ON
/// clause, or HAVING's own filter - never a SELECT list, ORDER BY, or GROUP BY
/// (docs/audit-remediation-plan.md Phase 3.1: a function/arithmetic wrap on a column that's
/// never used to filter rows isn't a sargability concern at all, since there's no seek to lose).
///
/// A Tier-1 pattern is purely syntactic by construction - it never needed types to FIRE - but
/// whether it's worth a reader's attention still depends on whether the column is even indexed:
/// `UPPER(Notes) = 'X'` on an unindexed nvarchar(max) costs nothing extra beyond the wrap
/// itself, there was no seek to lose. <see cref="SargabilityFinding.Indexed"/> resolves the
/// column through the same catalog/lineage machinery Pass 3/4 uses for typed findings
/// (<see cref="FromScopeResolver"/>, <see cref="ScalarExpressionResolver"/>), so a consumer can
/// tell "this predicate is on a real, indexed column" from "this predicate is on a column we
/// have no evidence is indexed" - the largest source of unranked noise this pass had before.
/// </summary>
public static class NonSargablePredicateScanner
{
    /// <summary>
    /// Real-world aggregate functions never lose "sargability" the way a scalar function wrap does -
    /// COUNT/SUM/AVG/etc. wrapping a column in a HAVING clause (the only place they can appear
    /// alongside a column reference) reflects per-group aggregation, not an avoidable index-
    /// defeating transform (docs/audit-remediation-plan.md Phase 3.1).
    /// </summary>
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    /// <summary>Scans with no catalog/lineage context available - every finding's <see cref="SargabilityFinding.Indexed"/> stays null (unresolved), never guessed. Used by callers that only need the syntactic pattern itself (Tier-1's own fixture tests) or that run before a catalog exists.</summary>
    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult) =>
        Scan(parseResult, new DatabaseCatalog(), new LineageCatalog(new Dictionary<string, ResolvedRelation>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new SkipLedger()));

    /// <summary>
    /// <paramref name="enclosingScope"/> seeds the proc/function/trigger scope a reparsed
    /// dynamic SQL fragment is considered inside, so a #temp table declared in the surrounding
    /// STATIC body resolves inside the dynamic text too - the reparsed fragment has no
    /// CREATE PROCEDURE wrapper of its own to discover the scope from. Null (the default) for
    /// an ordinary top-level scan. <paramref name="ledger"/> is where an unresolved scope
    /// reference (an unresolvable FROM table, CTE, or column) gets recorded - this pass used to
    /// pass <c>ledger: null</c> to every shared resolver it calls (FromScopeResolver/
    /// ScalarExpressionResolver/CteResolver), so a Tier-1 finding's own resolution failures were
    /// the one pass with zero trace of what it couldn't resolve, unlike every other pass. Null
    /// (the default) preserves the old silent behavior for callers that don't care.
    /// </summary>
    public static IReadOnlyList<SargabilityFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, DynamicSqlScope? enclosingScope = null, SkipLedger? ledger = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        ScanFull(parseResult, catalog, lineage, enclosingScope, ledger, callerScopeByCalleeScope).Findings;

    /// <summary>
    /// Same walk as <see cref="Scan(SqlParseResult, DatabaseCatalog, LineageCatalog, DynamicSqlScope?, SkipLedger?, IReadOnlyDictionary{string, IReadOnlyList{string}})"/>, also returning
    /// <see cref="TemporalBoundaryPrecisionFinding"/>s (the BETWEEN end-of-period boundary
    /// correctness check, docs/detection-checklist.md Tier 1 "Type-aware upgrade of the
    /// sargability stream") - a genuinely distinct finding family from
    /// <see cref="SargabilityFinding"/> (a correctness bug, not a lost seek), but reusing the
    /// SAME scope-resolution walk rather than a second, redundant AST pass over the same file.
    /// </summary>
    public static (IReadOnlyList<SargabilityFinding> Findings, IReadOnlyList<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings) ScanFull(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, DynamicSqlScope? enclosingScope = null, SkipLedger? ledger = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, lineage.AllRelations, enclosingScope, ledger, callerScopeByCalleeScope);
        visitor.SeedEnclosingScope(parseResult.Fragment);
        parseResult.Fragment.Accept(visitor);
        return (visitor.Findings, visitor.TemporalBoundaryFindings);
    }

    // CS9107: sourcePath/catalog/resolvedViews/ledger are used throughout this class's own body
    // (way beyond scope resolution) AND forwarded to ScopedSqlVisitorBase for its own, separate
    // CTE/scope bookkeeping - a deliberate, harmless double capture, not the accidental one this
    // warning exists to catch. See TypedPredicateExtractor's identical suppression for the full
    // rationale.
#pragma warning disable CS9107
    private sealed class Visitor(
        string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, DynamicSqlScope? enclosingScope = null,
        SkipLedger? ledger = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
        : ScopedSqlVisitorBase(sourcePath, catalog, resolvedViews, ledger, enclosingScope?.ProcScope, callerScopeByCalleeScope)
#pragma warning restore CS9107
    {
        private bool _inFilterContext;

        public List<SargabilityFinding> Findings { get; } = [];

        public List<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings { get; } = [];

        /// <summary>Mirrors TypedPredicateExtractor's identical seed - pushes the enclosing trigger's inserted/deleted pseudo-tables onto the CTE stack before the visitor starts walking, so a reparsed dynamic SQL fragment found inside a trigger body sees them too.</summary>
        public void SeedEnclosingScope(TSqlFragment rootFragment)
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                PushCteRelations(BuildTriggerPseudoTableRelations(target, rootFragment));
            }
        }

        /// <summary>
        /// Resets filter context to false for every part of a query specification except its
        /// own WHERE/HAVING (whose own overrides turn it back on) - without this, a WHERE
        /// clause's own nested subquery (an EXISTS/IN (SELECT ...)) would inherit "filter
        /// context = true" for that subquery's unrelated SELECT list, and a top-level SELECT
        /// list would inherit whatever the enclosing scope happened to be.
        /// </summary>
        public override void ExplicitVisit(QuerySpecification node)
        {
            ScopeStack.Push(FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext()));

            var previous = _inFilterContext;
            _inFilterContext = false;

            node.FromClause?.Accept(this);

            foreach (var element in node.SelectElements)
            {
                element.Accept(this);
            }

            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.HavingClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            node.WindowClause?.Accept(this);

            _inFilterContext = previous;
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        /// <summary>UPDATE/DELETE/MERGE predicates get the same FROM-scope resolution SELECT gets (docs/audit-remediation-plan.md Phase 4.1's coverage gap, mirrored here for index resolution).</summary>
        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            ScopeStack.Pop();
            PopCteScope();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));

            var previousFilterContext = _inFilterContext;
            _inFilterContext = true;
            base.ExplicitVisit(node);
            _inFilterContext = previousFilterContext;

            ScopeStack.Pop();
            PopCteScope();
        }

        // SelectStatement/UpdateStatement/DeleteStatement/MergeStatement above all push CTE
        // scope; INSERT had no override at all here (unlike TypedPredicateExtractor's identical
        // ExplicitVisit(InsertStatement)), so `WITH cte AS (...) INSERT INTO t SELECT ... FROM
        // cte WHERE UPPER(cte.Col) = 'x'` failed to resolve the `cte` alias in FromScopeResolver -
        // the syntactic FunctionWrappedColumn finding still fired (InspectSide/FindAnyColumn work
        // on the raw AST regardless of scope resolution), but Indexed silently resolved to
        // false/unresolved instead of true, understating the finding for ranking purposes. No
        // FROM scope is pushed for the INSERT target itself (mirrors TypedPredicateExtractor: an
        // INSERT target is never referenced by a predicate), only CTEs, so a CTE referenced by
        // the INSERT ... SELECT source still resolves.
        public override void ExplicitVisit(InsertStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        /// <summary>
        /// A JOIN's ON clause is a filter context exactly like WHERE; the table references it
        /// joins are not (a derived-table subquery there has its own SELECT list to protect).
        /// </summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _inFilterContext;
            _inFilterContext = true;
            node.SearchCondition?.Accept(this);
            _inFilterContext = previous;
        }


        public override void Visit(BooleanComparisonExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            // CHARINDEX/LEFT need the COMPARISON's own operator and other-side literal to tell a
            // rewritable prefix-match shape from a genuine substring search (docs/detection-
            // checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #3) - InspectSide
            // only ever sees one side in isolation, so this is intercepted here, before the
            // generic per-side dispatch, and skips the generic dispatch for whichever side it
            // already reported on (never double-reports the same wrap under two kinds).
            if (!TryAddCharindexOrLeftFinding(node.FirstExpression, node.SecondExpression, node.ComparisonType))
            {
                InspectSide(node.FirstExpression);
            }

            if (!TryAddCharindexOrLeftFinding(node.SecondExpression, node.FirstExpression, node.ComparisonType))
            {
                InspectSide(node.SecondExpression);
            }
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            // BETWEEN: "col BETWEEN a AND b" - the tested value is FirstExpression, but a
            // wrapped column can just as easily appear as a range BOUND ("'m' BETWEEN
            // LOWER(LowCode) AND HighCode") - all three positions get the same inspection.
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between
                || node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                InspectSide(node.FirstExpression);
                InspectSide(node.SecondExpression);
                InspectSide(node.ThirdExpression);
            }

            // Plain BETWEEN (not NotBetween - the imprecision-direction analysis differs and
            // wasn't oracle-probed) against a TIME/DATETIME2/DATETIMEOFFSET column is a distinct,
            // CORRECTNESS concern, not a sargability one - BETWEEN is perfectly sargable here.
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between)
            {
                TryAddTemporalBoundaryFinding(node);
            }
        }

        /// <summary>UPPER(col) IN (...) defeats the index exactly like UPPER(col) = '...' - the tested Expression side gets the identical dispatch a comparison's own operand gets. The Values list (and any Subquery) are not inspected: a wrapped literal/subquery element isn't a sargability concern the way the tested expression is.</summary>
        public override void Visit(InPredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            InspectSide(node.Expression);
        }

        public override void Visit(LikePredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            switch (node.SecondExpression)
            {
                case StringLiteral { Value: ['%', ..] } literal:
                    Add(SargabilityFindingKind.LeadingWildcardLike, columnName, literal.Value, node, columnRef);
                    break;
                case StringLiteral:
                    // A literal pattern with no leading wildcard is sargable; nothing to report.
                    break;
                default:
                    // The pattern isn't a literal (a parameter/variable/expression) - we can't
                    // rule out a leading wildcard statically. CLAUDE.md: "LIKE @p marked conditional".
                    Add(SargabilityFindingKind.LikePatternNotLiteral, columnName, detail: null, node, columnRef);
                    break;
            }
        }

        private void InspectSide(ScalarExpression expression)
        {
            // Unwrap defensive parens before dispatching - `(CASE WHEN Col = 'x' THEN 1 END) = 1`
            // is exactly as common in real-world SQL as the unparenthesized form (the Microsoft
            // Q&A repro FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql cites writes it exactly
            // this way), and every case below (CAST/CONVERT/BinaryExpression/CASE/COALESCE/
            // NULLIF/IIF) would otherwise silently miss it - a ParenthesisExpression node sitting
            // directly on top of the wrap defeats every `case SomeWrapType` match here even
            // though FindAnyColumn already unwraps parens fine one level down.
            while (expression is ParenthesisExpression parenthesized)
            {
                expression = parenthesized.Expression;
            }

            switch (expression)
            {
                case FunctionCall { Parameters.Count: > 0 } functionCall
                    when !AggregateFunctionNames.Contains(functionCall.FunctionName.Value) && FirstNamedColumn(functionCall.Parameters) is { } named:
                    InspectFunctionCall(functionCall, named);
                    break;

                case CastCall castCall when FindAnyColumn(castCall.Parameter) is { } found:
                    if (ResolveIndexInfo(found.Ref).TableQualifiedName is { } castTable
                        && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, castTable, castCall))
                    {
                        break;
                    }

                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CAST", castCall, found.Ref);
                    break;

                case ConvertCall convertCall when FindAnyColumn(convertCall.Parameter) is { } found:
                    if (ResolveIndexInfo(found.Ref).TableQualifiedName is { } convertTable
                        && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, convertTable, convertCall))
                    {
                        break;
                    }

                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CONVERT", convertCall, found.Ref);
                    break;

                case BinaryExpression binary:
                    InspectArithmetic(binary);
                    break;

                // CLAUDE.md's own named hard cases (CASE/COALESCE/NULLIF) plus IIF, the shorthand
                // for a two-branch CASE - none of these are FunctionCall nodes (a distinct
                // ScriptDom node type each), so they were invisible here even though ISNULL(...)
                // (which IS a FunctionCall) was already caught above. A column wrapped in any of
                // them is exactly as non-sargable as a column wrapped in a scalar function: the
                // engine can't seek through a CASE/COALESCE/NULLIF/IIF result any more than it can
                // through UPPER(col). Reused under the existing FunctionWrappedColumn kind rather
                // than inventing one per construct, matching how ISNULL already shares it with
                // every other function-wrapped-column case.
                //
                // Searches BOTH the value positions (THEN/ELSE/argument expressions, via
                // FindAnyColumn) AND, for CASE/IIF, the boolean test itself (a SearchedCaseExpression's
                // WhenExpression / IIfCall's Predicate, via FindAnyColumnInBoolean) - a real,
                // documented repro (Microsoft Q&A, Erland Sommarskog: "CASE expressions are not
                // sargable", `WHERE (CASE WHEN MobileNumber = 'x' THEN CAST(1 AS BIT) END) = 1`
                // measured as a 199-read index scan vs a 122-read seek for the unwrapped
                // equivalent) wraps the column in exactly the WHEN-test position, not the THEN
                // value - so both positions have to be covered to catch the shape this rule is
                // actually named for.
                case CoalesceExpression or NullIfExpression or IIfCall or SearchedCaseExpression or SimpleCaseExpression
                    when FindAnyColumn(expression) is { } wrapped:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, wrapped.Name, WrapConstructName(expression), expression, wrapped.Ref);
                    break;
            }
        }

        private void InspectFunctionCall(FunctionCall functionCall, (ColumnReferenceExpression Ref, string Name) named)
        {
            // JSON_VALUE/JSON_QUERY are the one function-wrap shape that ISN'T always a
            // lost seek (checklist "Corrections to shipped work" false-positive fix):
            // since SQL Server 2016 the engine can match the call to an indexed computed
            // column with an identical definition and seek on it instead of scanning.
            if (JsonComputedColumnMatcher.IsJsonPathFunction(functionCall.FunctionName.Value)
                && ResolveIndexInfo(named.Ref).TableQualifiedName is { } jsonTable
                && JsonComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, jsonTable, named.Name, functionCall))
            {
                return;
            }

            if (Rules.SargabilityClassifier.IsCaseFoldFunction(functionCall.FunctionName.Value))
            {
                AddCaseFold(functionCall, named);
                return;
            }

            if (string.Equals(functionCall.FunctionName.Value, "ISNULL", StringComparison.OrdinalIgnoreCase)
                && Rules.SargabilityClassifier.ShouldSuppressIsNullOnKnownNotNullColumn(functionCall.FunctionName.Value, IsKnownNotNullColumn(named.Ref)))
            {
                return;
            }

            // Named date-form rule (docs/detection-checklist.md Tier 1 "Type-aware
            // upgrade of the sargability stream" #2) - oracle-verified structurally
            // identical to case-folding: always forces a scan, no type/verdict question,
            // only the computed-column precision guard can suppress it.
            if (Rules.SargabilityClassifier.IsDateFunction(functionCall.FunctionName.Value))
            {
                if (ResolveIndexInfo(named.Ref).TableQualifiedName is { } dateTable
                    && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, dateTable, functionCall))
                {
                    return;
                }

                Add(SargabilityFindingKind.DateFunctionOnColumn, named.Name, functionCall.FunctionName.Value, functionCall, named.Ref);
                return;
            }

            Add(SargabilityFindingKind.FunctionWrappedColumn, named.Name, functionCall.FunctionName.Value, functionCall, named.Ref);
        }

        private static string WrapConstructName(ScalarExpression expression) => expression switch
        {
            CoalesceExpression => "COALESCE",
            NullIfExpression => "NULLIF",
            IIfCall => "IIF",
            CaseExpression => "CASE",
            _ => expression.GetType().Name,
        };

        private void InspectArithmetic(BinaryExpression binary)
        {
            var found = FindAnyColumn(binary.FirstExpression) ?? FindAnyColumn(binary.SecondExpression);
            if (found is { } column)
            {
                Add(SargabilityFindingKind.ColumnArithmetic, column.Name, binary.BinaryExpressionType.ToString(), binary, column.Ref);
            }
        }

        /// <summary>
        /// Recursively searches an expression subtree for the first genuine column reference -
        /// shared by CastOrConvertOnColumn and ColumnArithmetic so a wrapped column doesn't have
        /// to be the DIRECT parameter/operand to be caught (e.g. <c>CAST(ISNULL(col, 0) AS
        /// int)</c>). Deliberately does not descend into a subquery (<see cref="ScalarSubquery"/>
        /// isn't a case here at all, since it's not a <see cref="ScalarExpression"/> subtype this
        /// switch matches) - a column inside a nested SELECT belongs to that subquery's own
        /// filter context, not this one.
        /// </summary>
        private static (ColumnReferenceExpression Ref, string Name)? FindAnyColumn(ScalarExpression expression) => expression switch
        {
            ColumnReferenceExpression columnRef when ColumnName(columnRef) is { } name => (columnRef, name),
            ParenthesisExpression parenthesis => FindAnyColumn(parenthesis.Expression),
            UnaryExpression unary => FindAnyColumn(unary.Expression),
            CastCall castCall => FindAnyColumn(castCall.Parameter),
            ConvertCall convertCall => FindAnyColumn(convertCall.Parameter),
            BinaryExpression binary => FindAnyColumn(binary.FirstExpression) ?? FindAnyColumn(binary.SecondExpression),
            FunctionCall functionCall => functionCall.Parameters.Select(FindAnyColumn).FirstOrDefault(r => r is not null),
            CoalesceExpression coalesce => coalesce.Expressions.Select(FindAnyColumn).FirstOrDefault(r => r is not null),
            NullIfExpression nullIf => FindAnyColumn(nullIf.FirstExpression) ?? FindAnyColumn(nullIf.SecondExpression),
            // IIF's own boolean test is searched too, not just its Then/Else values - see the
            // CASE case below for why (same construct, shorthand two-branch form).
            IIfCall iif => FindAnyColumnInBoolean(iif.Predicate) ?? FindAnyColumn(iif.ThenExpression) ?? FindAnyColumn(iif.ElseExpression),
            SimpleCaseExpression simpleCase => FindAnyColumn(simpleCase.InputExpression)
                ?? simpleCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (simpleCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),
            // Searches each WHEN's own boolean test (FindAnyColumnInBoolean) as well as every
            // THEN/ELSE value - a real, documented repro (Microsoft Q&A, Erland Sommarskog)
            // wraps the column in exactly the WHEN-test position (`CASE WHEN MobileNumber = 'x'
            // THEN CAST(1 AS BIT) END = 1`), not the THEN value, so only searching THEN/ELSE
            // would miss the shape this rule is actually named for.
            SearchedCaseExpression searchedCase => searchedCase.WhenClauses.Select(w => FindAnyColumnInBoolean(w.WhenExpression)).FirstOrDefault(r => r is not null)
                ?? searchedCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (searchedCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),
            _ => null,
        };

        /// <summary>
        /// Mirrors <see cref="FindAnyColumn"/> but over the boolean-expression grammar (a CASE
        /// WHEN test, an IIF predicate) rather than the scalar-expression one - a column
        /// referenced only inside the boolean TEST that decides which branch to take is just as
        /// wrapped/non-sargable as one in the THEN/ELSE value (both are inside the same opaque
        /// CASE/IIF the engine can't seek through). Deliberately does not descend into a
        /// subquery-bearing predicate (EXISTS/IN/quantified comparison) - a column inside a
        /// nested SELECT belongs to that subquery's own filter context, not this one, matching
        /// <see cref="FindAnyColumn"/>'s own subquery exclusion.
        /// </summary>
        private static (ColumnReferenceExpression Ref, string Name)? FindAnyColumnInBoolean(BooleanExpression expression) => expression switch
        {
            BooleanComparisonExpression comparison => FindAnyColumn(comparison.FirstExpression) ?? FindAnyColumn(comparison.SecondExpression),
            BooleanBinaryExpression binary => FindAnyColumnInBoolean(binary.FirstExpression) ?? FindAnyColumnInBoolean(binary.SecondExpression),
            BooleanNotExpression not => FindAnyColumnInBoolean(not.Expression),
            BooleanParenthesisExpression parenthesis => FindAnyColumnInBoolean(parenthesis.Expression),
            BooleanIsNullExpression isNull => FindAnyColumn(isNull.Expression),
            _ => null,
        };

        /// <summary>The first parameter that's a genuine named column reference - COUNT(*) etc. have a Wildcard ColumnReferenceExpression with no MultiPartIdentifier, which isn't "a column" for this rule's purposes.</summary>
        private static (ColumnReferenceExpression Ref, string Name)? FirstNamedColumn(IList<ScalarExpression> parameters)
        {
            foreach (var parameter in parameters.OfType<ColumnReferenceExpression>())
            {
                if (ColumnName(parameter) is { } name)
                {
                    return (parameter, name);
                }
            }

            return null;
        }

        private static string? ColumnName(ColumnReferenceExpression columnRef) =>
            columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers ? identifiers[^1].Value : null;

        private void Add(SargabilityFindingKind kind, string columnName, string? detail, TSqlFragment node, ColumnReferenceExpression columnRef)
        {
            var (tableQualifiedName, indexed, _) = ResolveIndexInfo(columnRef);
            Findings.Add(new SargabilityFinding(
                kind, columnName, detail, sourcePath, node.StartLine, node.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Common.FragmentTextRenderer.Render(node)));
        }

        /// <summary>
        /// True only when the column resolves to a real catalog column that is PROVABLY NOT
        /// NULL (a real, resolved, non-nullable catalog column) - false for anything unresolved
        /// or genuinely nullable, never a guess either way.
        /// </summary>
        private bool IsKnownNotNullColumn(ColumnReferenceExpression columnRef)
        {
            var (tableQualifiedName, _, _) = ResolveIndexInfo(columnRef);
            if (tableQualifiedName is not { } table || ColumnName(columnRef) is not { } columnName)
            {
                return false;
            }

            var column = catalog.Find(table, CurrentProcScope)?.FindColumn(columnName);
            return column is { IsNullable: false };
        }

        /// <summary>
        /// <c>CHARINDEX(x, col) = 1</c> / <c>LEFT(col, n) = 'x'</c> (with <c>LEN('x') == n</c>)
        /// are both exactly equivalent to <c>col LIKE 'x%'</c> - a real, always-usable sargable
        /// rewrite. Any other comparison against CHARINDEX/LEFT still wraps the column (still
        /// non-sargable, still reported) but has no such rewrite - a genuine substring search.
        /// Returns false (and adds nothing) when <paramref name="candidateSide"/> isn't a
        /// CHARINDEX/LEFT call wrapping a real column at all, so the caller falls back to the
        /// generic per-side dispatch for it.
        /// </summary>
        private bool TryAddCharindexOrLeftFinding(ScalarExpression candidateSide, ScalarExpression otherSide, BooleanComparisonType comparisonType)
        {
            while (candidateSide is ParenthesisExpression parenthesized)
            {
                candidateSide = parenthesized.Expression;
            }

            while (otherSide is ParenthesisExpression otherParenthesized)
            {
                otherSide = otherParenthesized.Expression;
            }

            // ScriptDOM gives LEFT its own dedicated node type (LeftFunctionCall), NOT a generic
            // FunctionCall the way CHARINDEX gets - found live probing the parser directly (RIGHT
            // presumably gets the identical treatment, but this rule only ever needed LEFT).
            if (candidateSide is LeftFunctionCall leftCall)
            {
                return TryAddLeftFinding(leftCall, otherSide, comparisonType);
            }

            if (candidateSide is not FunctionCall { Parameters.Count: > 0 } functionCall
                || !string.Equals(functionCall.FunctionName.Value, "CHARINDEX", StringComparison.OrdinalIgnoreCase)
                || FirstNamedColumn(functionCall.Parameters) is not { } named)
            {
                return false;
            }

            var isExactPrefixMatch = comparisonType == BooleanComparisonType.Equals && otherSide is IntegerLiteral { Value: "1" };
            var detail = Rules.SargabilityClassifier.DescribeCharindexRemediation(isExactPrefixMatch);

            if (ResolveIndexInfo(named.Ref).TableQualifiedName is { } table
                && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, functionCall))
            {
                return true;
            }

            Add(SargabilityFindingKind.CharindexOrLeftOnColumn, named.Name, detail, functionCall, named.Ref);
            return true;
        }

        private bool TryAddLeftFinding(LeftFunctionCall leftCall, ScalarExpression otherSide, BooleanComparisonType comparisonType)
        {
            if (leftCall.Parameters is not [{ } columnCandidate, IntegerLiteral { Value: { } lengthText }]
                || FirstNamedColumn([columnCandidate]) is not { } named)
            {
                return false;
            }

            var isExactPrefixMatch = comparisonType == BooleanComparisonType.Equals
                && otherSide is StringLiteral { Value: { } literalValue } && literalValue.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) == lengthText;
            var detail = Rules.SargabilityClassifier.DescribeLeftRemediation(isExactPrefixMatch);

            if (ResolveIndexInfo(named.Ref).TableQualifiedName is { } table
                && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, leftCall))
            {
                return true;
            }

            Add(SargabilityFindingKind.CharindexOrLeftOnColumn, named.Name, detail, leftCall, named.Ref);
            return true;
        }

        /// <summary>
        /// Fires only when: the tested value is a real, resolved column whose catalog type is
        /// TIME/DATETIME2/DATETIMEOFFSET with a known declared scale (fractional-seconds
        /// precision), AND the BETWEEN's upper bound is a string literal whose own fractional-
        /// second digit count is LESS than that declared scale - the exact, oracle-confirmed
        /// mechanism by which rows in the precision gap are silently excluded. Never fires on an
        /// unresolved column or a non-literal bound (never a guess).
        /// </summary>
        private void TryAddTemporalBoundaryFinding(BooleanTernaryExpression node)
        {
            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            if (node.ThirdExpression is not StringLiteral { Value: { } upperBoundText })
            {
                return;
            }

            var (tableQualifiedName, _, type) = ResolveIndexInfo(columnRef);
            if (tableQualifiedName is not { } table)
            {
                return;
            }

            if (type is not { Category: SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTimeOffset or SqlTypeCategory.Time, Scale: { } columnScale })
            {
                return;
            }

            if (!Rules.TemporalBoundaryClassifier.HasInsufficientFractionalPrecision(columnScale, upperBoundText, out var fractionalDigits))
            {
                return;
            }

            TemporalBoundaryFindings.Add(new TemporalBoundaryPrecisionFinding(
                table, columnName, columnScale, fractionalDigits, upperBoundText, sourcePath, node.StartLine, node.StartColumn));
        }

        private void AddCaseFold(FunctionCall functionCall, (ColumnReferenceExpression Ref, string Name) named)
        {
            var (tableQualifiedName, indexed, type) = ResolveIndexInfo(named.Ref);

            if (tableQualifiedName is { } table && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, functionCall))
            {
                return;
            }

            var detail = Rules.SargabilityClassifier.DescribeCaseFoldRemediation(functionCall.FunctionName.Value, type?.Collation);

            Findings.Add(new SargabilityFinding(
                SargabilityFindingKind.CaseFoldOnColumn, named.Name, detail, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Common.FragmentTextRenderer.Render(functionCall)));
        }

        /// <summary>
        /// Resolves <paramref name="columnRef"/> through the current scope chain to find out
        /// whether it's a real, leading-key-indexed catalog column - the same resolution
        /// TypedPredicateExtractor performs for typed findings, reused here so a syntactic
        /// finding carries the same "is this actually worth a reader's attention" signal. Never
        /// guesses: any provenance other than a direct BaseColumn/Declared passthrough (a
        /// CAST-derived column, a UNION branch, an unresolvable reference) reports
        /// TableQualifiedName=null/Indexed=null/Type=null rather than assuming either answer.
        /// <paramref name="columnRef"/>'s own resolved <see cref="SqlType"/> - already computed by
        /// <c>ScalarExpressionResolver.ResolveColumnReference</c> as part of resolving the
        /// provenance below - is returned alongside, so a caller that also needs the column's type
        /// (case-fold collation, the temporal-boundary scale check) never re-queries the catalog a
        /// second time for data this call already has in hand. Callers that also need a fact this
        /// method does NOT resolve (nullability, which lives on the catalog column, not on
        /// <see cref="SqlType"/> itself) still make their own single, additional catalog call.
        /// </summary>
        private (string? TableQualifiedName, bool? Indexed, SqlType? Type) ResolveIndexInfo(ColumnReferenceExpression columnRef)
        {
            if (ScopeStack.Count == 0)
            {
                return (null, null, null);
            }

            var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger);

            return provenance switch
            {
                // A table this pass never resolved at all (Unknown, never a guess) is distinct
                // from a resolved table with no matching index (false) - the `?? false` this
                // used to fall back to collapsed both into "not indexed," ranking an
                // unresolvable-catalog finding as confirmed noise instead of Unknown.
                ColumnProvenance.BaseColumn baseColumn => (
                    baseColumn.TableQualifiedName,
                    catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.IsIndexedColumn(baseColumn.ColumnName),
                    baseColumn.Type),

                // A multi-statement TVF's own RETURNS TABLE(...) column has no real backing
                // table, so TableQualifiedName stays null there - but a trigger's inserted/
                // deleted DOES carry one (FromScopeResolver.ToPseudoTableRelation keeps the
                // real target table's name so the finding stays attributable to where the data
                // actually lives), so it must not be discarded the way TypedPredicateExtractor's
                // identical Declared case doesn't discard it either. Indexed is always false
                // regardless: neither shape is backed by a real catalog index.
                ColumnProvenance.Declared declared => (declared.TableQualifiedName, false, declared.Type),

                _ => (null, null, null),
            };
        }

    }
}
