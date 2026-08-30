using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
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
        var visitor = new Visitor(parseResult.SourcePath, catalog, lineage.AllRelations, enclosingScope, ledger, callerScopeByCalleeScope);
        visitor.SeedEnclosingScope(parseResult.Fragment);
        parseResult.Fragment.Accept(visitor);
        return (visitor.Findings, visitor.TemporalBoundaryFindings);
    }

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

        public void SeedEnclosingScope(TSqlFragment rootFragment)
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                PushCteRelations(BuildTriggerPseudoTableRelations(target, rootFragment));
            }
        }

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
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
        }

        protected override void OnMergeStatementScope(MergeStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            var previousFilterContext = _inFilterContext;
            _inFilterContext = true;
            continueDescent();
            _inFilterContext = previousFilterContext;
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            WithPredicateLocation(node.SearchCondition, () => node.AcceptChildren(this));
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            WithPredicateLocation(node.SearchCondition, () => node.AcceptChildren(this));
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _inFilterContext;
            _inFilterContext = true;
            WithPredicateLocation(node.SearchCondition, () => node.SearchCondition?.Accept(this));
            _inFilterContext = previous;
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            if (IsDeadPredicate(node))
            {
                ledger?.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

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

            if (IsDeadPredicate(node))
            {
                ledger?.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
                return;
            }

            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between
                || node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                InspectSide(node.FirstExpression);
                InspectSide(node.SecondExpression);
                InspectSide(node.ThirdExpression);
            }

            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between)
            {
                TryAddTemporalBoundaryFinding(node);
            }
        }

        public override void Visit(InPredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            if (IsDeadPredicate(node))
            {
                ledger?.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
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

            if (IsDeadPredicate(node))
            {
                ledger?.Record(AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, NormalizationEliminatedConstructKind, NormalizationEliminatedLedgerReason);
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

                    break;
                default:

                    Add(SargabilityFindingKind.LikePatternNotLiteral, columnName, detail: null, node, columnRef);
                    break;
            }
        }

        private void InspectSide(ScalarExpression expression)
        {

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

                case CoalesceExpression or NullIfExpression or IIfCall or SearchedCaseExpression or SimpleCaseExpression
                    when FindAnyColumn(expression) is { } wrapped:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, wrapped.Name, WrapConstructName(expression), expression, wrapped.Ref);
                    break;
            }
        }

        private void InspectFunctionCall(FunctionCall functionCall, (ColumnReferenceExpression Ref, string Name) named)
        {

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

        private void Add(SargabilityFindingKind kind, string columnName, string? detail, TSqlFragment node, ColumnReferenceExpression columnRef)
        {
            var (tableQualifiedName, indexed, _) = ResolveIndexInfo(columnRef);
            Findings.Add(new SargabilityFinding(
                kind, columnName, detail, sourcePath, node.StartLine, node.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Common.FragmentTextRenderer.Render(node)));
        }

        private bool IsKnownNotNullColumn(ColumnReferenceExpression columnRef)
        {
            var (tableQualifiedName, _, _) = ResolveIndexInfo(columnRef);
            if (tableQualifiedName is not { } table || ColumnName(columnRef) is not { } columnName)
            {
                return false;
            }

            var column = catalog.Find(table, CurrentProcScope)?.FindColumn(columnName, catalog.IdentifierComparer);
            return column is { IsNullable: false };
        }

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

        private (string? TableQualifiedName, bool? Indexed, SqlType? Type) ResolveIndexInfo(ColumnReferenceExpression columnRef)
        {
            if (ScopeStack.Count == 0)
            {
                return (null, null, null);
            }

            var scopeChain = CurrentScopeChain();
            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger, catalog);

            return provenance switch
            {

                ColumnProvenance.BaseColumn baseColumn => (
                    baseColumn.TableQualifiedName,
                    catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.IsIndexedColumn(baseColumn.ColumnName, catalog.IdentifierComparer),
                    baseColumn.Type),

                ColumnProvenance.Declared declared => (declared.TableQualifiedName, false, declared.Type),

                _ => (null, null, null),
            };
        }

    }
}
