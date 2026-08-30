using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class NonSargablePredicateScanner
{
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult) =>
        Scan(parseResult, new DatabaseCatalog(), new LineageCatalog(new Dictionary<string, ResolvedRelation>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new SkipLedger()));

    public static IReadOnlyList<SargabilityFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, DynamicSqlScope? enclosingScope = null, SkipLedger? ledger = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null) =>
        ScanFull(parseResult, catalog, lineage, enclosingScope, ledger, callerScopeByCalleeScope).Findings;

    public static (IReadOnlyList<SargabilityFinding> Findings, IReadOnlyList<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings) ScanFull(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, DynamicSqlScope? enclosingScope = null, SkipLedger? ledger = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope = null)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(
            parseResult.SourcePath, catalog, lineage.AllRelations, ledger, enclosingScope?.ProcScope, callerScopeByCalleeScope, rules: [rule]);

        if (enclosingScope?.TriggerTarget is { } target)
        {
            walker.PushCteRelations(walker.BuildTriggerPseudoTableRelations(target, parseResult.Fragment));
        }

        parseResult.Fragment.Accept(walker);
        return (rule.Findings, rule.TemporalBoundaryFindings);
    }

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<SargabilityFinding> Findings { get; } = [];

        public List<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, (condition, chain) => InspectCondition(condition, chain, walker));

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, (condition, chain) => InspectCondition(condition, chain, walker));

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, (condition, chain) => InspectCondition(condition, chain, walker));

        public void OnEnterMergeStatementScope(MergeStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var collector = new PredicateLeafCollector();
            node.Accept(collector);
            foreach (var leaf in collector.Leaves)
            {
                InspectLeaf(leaf, scopeChain, dead: false, walker);
            }
        }

        private void InspectCondition(BooleanExpression condition, ScopeChain scopeChain, ModuleWalker walker)
        {
            var dead = PredicateSurvivalAnalyzer.FindDeadComparisons(condition, columnRef => walker.ResolveColumnFacts(columnRef, scopeChain));

            var collector = new PredicateLeafCollector();
            condition.Accept(collector);
            foreach (var leaf in collector.Leaves)
            {
                InspectLeaf(leaf, scopeChain, dead.Contains(leaf), walker);
            }
        }

        private void InspectLeaf(TSqlFragment leaf, ScopeChain scopeChain, bool dead, ModuleWalker walker)
        {
            if (dead)
            {
                walker.Ledger?.Record(AnalysisPass.Predicates, sourcePath, leaf.StartLine, leaf.StartColumn, ModuleWalker.NormalizationEliminatedConstructKind, ModuleWalker.NormalizationEliminatedLedgerReason);
                return;
            }

            switch (leaf)
            {
                case BooleanComparisonExpression comparison:
                    InspectComparison(comparison, scopeChain, walker);
                    break;

                case BooleanTernaryExpression ternary:
                    InspectTernary(ternary, scopeChain, walker);
                    break;

                case InPredicate inPredicate:
                    InspectSide(inPredicate.Expression, scopeChain, walker);
                    break;

                case LikePredicate likePredicate:
                    InspectLikePredicate(likePredicate, scopeChain, walker);
                    break;
            }
        }

        private void InspectComparison(BooleanComparisonExpression node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (!TryAddCharindexOrLeftFinding(node.FirstExpression, node.SecondExpression, node.ComparisonType, scopeChain, walker))
            {
                InspectSide(node.FirstExpression, scopeChain, walker);
            }

            if (!TryAddCharindexOrLeftFinding(node.SecondExpression, node.FirstExpression, node.ComparisonType, scopeChain, walker))
            {
                InspectSide(node.SecondExpression, scopeChain, walker);
            }
        }

        private void InspectTernary(BooleanTernaryExpression node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between
                || node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                InspectSide(node.FirstExpression, scopeChain, walker);
                InspectSide(node.SecondExpression, scopeChain, walker);
                InspectSide(node.ThirdExpression, scopeChain, walker);
            }

            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between)
            {
                TryAddTemporalBoundaryFinding(node, scopeChain, walker);
            }
        }

        private void InspectLikePredicate(LikePredicate node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            switch (node.SecondExpression)
            {
                case StringLiteral { Value: ['%', ..] } literal:
                    Add(SargabilityFindingKind.LeadingWildcardLike, columnName, literal.Value, node, columnRef, scopeChain, walker);
                    break;
                case StringLiteral:

                    break;
                default:

                    Add(SargabilityFindingKind.LikePatternNotLiteral, columnName, detail: null, node, columnRef, scopeChain, walker);
                    break;
            }
        }

        private void InspectSide(ScalarExpression expression, ScopeChain scopeChain, ModuleWalker walker)
        {

            while (expression is ParenthesisExpression parenthesized)
            {
                expression = parenthesized.Expression;
            }

            switch (expression)
            {
                case FunctionCall { Parameters.Count: > 0 } functionCall
                    when !AggregateFunctionNames.Contains(functionCall.FunctionName.Value) && FirstNamedColumn(functionCall.Parameters) is { } named:
                    InspectFunctionCall(functionCall, named, scopeChain, walker);
                    break;

                case CastCall castCall when FindAnyColumn(castCall.Parameter) is { } found:
                    if (ResolveIndexInfo(found.Ref, scopeChain, walker).TableQualifiedName is { } castTable
                        && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, castTable, castCall))
                    {
                        break;
                    }

                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CAST", castCall, found.Ref, scopeChain, walker);
                    break;

                case ConvertCall convertCall when FindAnyColumn(convertCall.Parameter) is { } found:
                    if (ResolveIndexInfo(found.Ref, scopeChain, walker).TableQualifiedName is { } convertTable
                        && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, convertTable, convertCall))
                    {
                        break;
                    }

                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CONVERT", convertCall, found.Ref, scopeChain, walker);
                    break;

                case BinaryExpression binary:
                    InspectArithmetic(binary, scopeChain, walker);
                    break;

                case CoalesceExpression or NullIfExpression or IIfCall or SearchedCaseExpression or SimpleCaseExpression
                    when FindAnyColumn(expression) is { } wrapped:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, wrapped.Name, WrapConstructName(expression), expression, wrapped.Ref, scopeChain, walker);
                    break;
            }
        }

        private void InspectFunctionCall(FunctionCall functionCall, (ColumnReferenceExpression Ref, string Name) named, ScopeChain scopeChain, ModuleWalker walker)
        {

            if (JsonComputedColumnMatcher.IsJsonPathFunction(functionCall.FunctionName.Value)
                && ResolveIndexInfo(named.Ref, scopeChain, walker).TableQualifiedName is { } jsonTable
                && JsonComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, jsonTable, named.Name, functionCall))
            {
                return;
            }

            if (Rules.SargabilityClassifier.IsCaseFoldFunction(functionCall.FunctionName.Value))
            {
                AddCaseFold(functionCall, named, scopeChain, walker);
                return;
            }

            if (string.Equals(functionCall.FunctionName.Value, "ISNULL", StringComparison.OrdinalIgnoreCase)
                && Rules.SargabilityClassifier.ShouldSuppressIsNullOnKnownNotNullColumn(functionCall.FunctionName.Value, IsKnownNotNullColumn(named.Ref, scopeChain, walker)))
            {
                return;
            }

            if (Rules.SargabilityClassifier.IsDateFunction(functionCall.FunctionName.Value))
            {
                if (ResolveIndexInfo(named.Ref, scopeChain, walker).TableQualifiedName is { } dateTable
                    && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, dateTable, functionCall))
                {
                    return;
                }

                Add(SargabilityFindingKind.DateFunctionOnColumn, named.Name, functionCall.FunctionName.Value, functionCall, named.Ref, scopeChain, walker);
                return;
            }

            Add(SargabilityFindingKind.FunctionWrappedColumn, named.Name, functionCall.FunctionName.Value, functionCall, named.Ref, scopeChain, walker);
        }

        private static string WrapConstructName(ScalarExpression expression) => expression switch
        {
            CoalesceExpression => "COALESCE",
            NullIfExpression => "NULLIF",
            IIfCall => "IIF",
            CaseExpression => "CASE",
            _ => expression.GetType().Name,
        };

        private void InspectArithmetic(BinaryExpression binary, ScopeChain scopeChain, ModuleWalker walker)
        {
            var found = FindAnyColumn(binary.FirstExpression) ?? FindAnyColumn(binary.SecondExpression);
            if (found is { } column)
            {
                Add(SargabilityFindingKind.ColumnArithmetic, column.Name, binary.BinaryExpressionType.ToString(), binary, column.Ref, scopeChain, walker);
            }
        }

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

            IIfCall iif => FindAnyColumnInBoolean(iif.Predicate) ?? FindAnyColumn(iif.ThenExpression) ?? FindAnyColumn(iif.ElseExpression),
            SimpleCaseExpression simpleCase => FindAnyColumn(simpleCase.InputExpression)
                ?? simpleCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (simpleCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),

            SearchedCaseExpression searchedCase => searchedCase.WhenClauses.Select(w => FindAnyColumnInBoolean(w.WhenExpression)).FirstOrDefault(r => r is not null)
                ?? searchedCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (searchedCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),
            _ => null,
        };

        private static (ColumnReferenceExpression Ref, string Name)? FindAnyColumnInBoolean(BooleanExpression expression) => expression switch
        {
            BooleanComparisonExpression comparison => FindAnyColumn(comparison.FirstExpression) ?? FindAnyColumn(comparison.SecondExpression),
            BooleanBinaryExpression binary => FindAnyColumnInBoolean(binary.FirstExpression) ?? FindAnyColumnInBoolean(binary.SecondExpression),
            BooleanNotExpression not => FindAnyColumnInBoolean(not.Expression),
            BooleanParenthesisExpression parenthesis => FindAnyColumnInBoolean(parenthesis.Expression),
            BooleanIsNullExpression isNull => FindAnyColumn(isNull.Expression),
            _ => null,
        };

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

        private void Add(SargabilityFindingKind kind, string columnName, string? detail, TSqlFragment node, ColumnReferenceExpression columnRef, ScopeChain scopeChain, ModuleWalker walker)
        {
            var (tableQualifiedName, indexed, _) = ResolveIndexInfo(columnRef, scopeChain, walker);
            Findings.Add(new SargabilityFinding(
                kind, columnName, detail, sourcePath, node.StartLine, node.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Common.FragmentTextRenderer.Render(node)));
        }

        private bool IsKnownNotNullColumn(ColumnReferenceExpression columnRef, ScopeChain scopeChain, ModuleWalker walker)
        {
            var (tableQualifiedName, _, _) = ResolveIndexInfo(columnRef, scopeChain, walker);
            if (tableQualifiedName is not { } table || ColumnName(columnRef) is not { } columnName)
            {
                return false;
            }

            var column = catalog.Find(table, walker.CurrentProcScope)?.FindColumn(columnName, catalog.IdentifierComparer);
            return column is { IsNullable: false };
        }

        private bool TryAddCharindexOrLeftFinding(ScalarExpression candidateSide, ScalarExpression otherSide, BooleanComparisonType comparisonType, ScopeChain scopeChain, ModuleWalker walker)
        {
            while (candidateSide is ParenthesisExpression parenthesized)
            {
                candidateSide = parenthesized.Expression;
            }

            while (otherSide is ParenthesisExpression otherParenthesized)
            {
                otherSide = otherParenthesized.Expression;
            }

            if (candidateSide is LeftFunctionCall leftCall)
            {
                return TryAddLeftFinding(leftCall, otherSide, comparisonType, scopeChain, walker);
            }

            if (candidateSide is not FunctionCall { Parameters.Count: > 0 } functionCall
                || !string.Equals(functionCall.FunctionName.Value, "CHARINDEX", StringComparison.OrdinalIgnoreCase)
                || FirstNamedColumn(functionCall.Parameters) is not { } named)
            {
                return false;
            }

            var isExactPrefixMatch = comparisonType == BooleanComparisonType.Equals && otherSide is IntegerLiteral { Value: "1" };
            var detail = Rules.SargabilityClassifier.DescribeCharindexRemediation(isExactPrefixMatch);

            if (ResolveIndexInfo(named.Ref, scopeChain, walker).TableQualifiedName is { } table
                && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, functionCall))
            {
                return true;
            }

            Add(SargabilityFindingKind.CharindexOrLeftOnColumn, named.Name, detail, functionCall, named.Ref, scopeChain, walker);
            return true;
        }

        private bool TryAddLeftFinding(LeftFunctionCall leftCall, ScalarExpression otherSide, BooleanComparisonType comparisonType, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (leftCall.Parameters is not [{ } columnCandidate, IntegerLiteral { Value: { } lengthText }]
                || FirstNamedColumn([columnCandidate]) is not { } named)
            {
                return false;
            }

            var isExactPrefixMatch = comparisonType == BooleanComparisonType.Equals
                && otherSide is StringLiteral { Value: { } literalValue } && literalValue.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) == lengthText;
            var detail = Rules.SargabilityClassifier.DescribeLeftRemediation(isExactPrefixMatch);

            if (ResolveIndexInfo(named.Ref, scopeChain, walker).TableQualifiedName is { } table
                && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, leftCall))
            {
                return true;
            }

            Add(SargabilityFindingKind.CharindexOrLeftOnColumn, named.Name, detail, leftCall, named.Ref, scopeChain, walker);
            return true;
        }

        private void TryAddTemporalBoundaryFinding(BooleanTernaryExpression node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            if (node.ThirdExpression is not StringLiteral { Value: { } upperBoundText })
            {
                return;
            }

            var (tableQualifiedName, _, type) = ResolveIndexInfo(columnRef, scopeChain, walker);
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

        private void AddCaseFold(FunctionCall functionCall, (ColumnReferenceExpression Ref, string Name) named, ScopeChain scopeChain, ModuleWalker walker)
        {
            var (tableQualifiedName, indexed, type) = ResolveIndexInfo(named.Ref, scopeChain, walker);

            if (tableQualifiedName is { } table && ComputedColumnMatcher.HasIndexedMatchingComputedColumn(catalog, table, functionCall))
            {
                return;
            }

            var detail = Rules.SargabilityClassifier.DescribeCaseFoldRemediation(functionCall.FunctionName.Value, type?.Collation);

            Findings.Add(new SargabilityFinding(
                SargabilityFindingKind.CaseFoldOnColumn, named.Name, detail, sourcePath, functionCall.StartLine, functionCall.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Common.FragmentTextRenderer.Render(functionCall)));
        }

        private (string? TableQualifiedName, bool? Indexed, SqlType? Type) ResolveIndexInfo(ColumnReferenceExpression columnRef, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (scopeChain.Count == 0)
            {
                return (null, null, null);
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, walker.Ledger, catalog);

            var result = provenance switch
            {

                ColumnProvenance.BaseColumn baseColumn => (
                    baseColumn.TableQualifiedName,
                    catalog.Find(baseColumn.TableQualifiedName, walker.CurrentProcScope)?.IsIndexedColumn(baseColumn.ColumnName, catalog.IdentifierComparer),
                    baseColumn.Type),

                ColumnProvenance.Declared declared => (declared.TableQualifiedName, (bool?)false, declared.Type),

                _ => ((string?)null, (bool?)null, (SqlType?)null),
            };

            return result;
        }

        private sealed class PredicateLeafCollector : TSqlFragmentVisitor
        {
            public List<TSqlFragment> Leaves { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                Leaves.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(BooleanTernaryExpression node)
            {
                Leaves.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(InPredicate node)
            {
                Leaves.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(LikePredicate node)
            {
                Leaves.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
