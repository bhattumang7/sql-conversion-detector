using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Catalog;

/// <summary>
/// A static, deliberately incomplete scan for the SQL 2019+ scalar-UDF-inlining (FROID) blocker
/// list documented in docs/detection-reference.md Appendix 3. This is a body scan producing an
/// EXPLANATION, never the sole basis for asserting <c>NotInlineable</c> - the engine's own
/// <c>sys.sql_modules.is_inlineable</c> flag (live mode) is always preferred where available.
/// Encodes exactly the closed list Appendix 3 states; do not generalize beyond it - a blocker
/// this scanner doesn't recognize means <c>Unknown</c>, never a guessed "inlineable".
/// </summary>
public static class ScalarUdfInlineabilityScanner
{
    private static readonly HashSet<string> TimeDependentIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "CURRENT_TIMESTAMP",
    };

    /// <summary>
    /// Returns a human-readable blocker reason for the first Appendix-3 pattern found in
    /// <paramref name="body"/>, or null when the scan found nothing - which must be read as
    /// "this scan's closed list found no blocker", not "this function is inlineable". The
    /// "references a non-inlineable UDF" blocker checks only one level deep against
    /// <paramref name="catalog"/> (whatever it already knows at this point in file-declaration
    /// order) - a callee this catalog hasn't seen yet, or a chain more than one call deep, is
    /// deliberately left for the callee's own record to speak for itself rather than guessed at
    /// here.
    /// </summary>
    public static string? FindBlocker(
        StatementList? body, string ownQualifiedName, DatabaseCatalog catalog, IList<ProcedureParameter>? parameters = null)
    {
        // A table-valued parameter (always READONLY - the only T-SQL context that modifier is
        // legal in) checked before the body scan even runs, the same catalog-based signal
        // RegisterProcedureParameters already uses elsewhere for the identical question. Oracle-
        // confirmed directly (real Docker probe): a scalar UDF taking a TVP reports
        // is_inlineable = 0 regardless of what the body itself does with it.
        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.DataType is UserDataTypeReference userType
                    && catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is { Kind: CatalogTableKind.TableType })
                {
                    return $"table-valued parameter {parameter.VariableName.Value}";
                }
            }
        }

        if (body is null)
        {
            return null;
        }

        var visitor = new Visitor(ownQualifiedName, catalog);
        body.Accept(visitor);
        return visitor.Blocker;
    }

    private sealed class Visitor(string ownQualifiedName, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private int _returnStatementCount;

        public string? Blocker { get; private set; }

        public override void ExplicitVisit(WhileStatement node)
        {
            Report("WHILE loop");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            Report("TRY/CATCH");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            Report("table variable declaration");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            Report("EXECUTE statement");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            Report("cursor declaration");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ReturnStatement node)
        {
            _returnStatementCount++;
            if (_returnStatementCount > 1)
            {
                Report("multiple RETURN statements");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GoToStatement node)
        {
            Report("GOTO statement");
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(LabelStatement node)
        {
            Report("GOTO statement");
            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Oracle-confirmed directly (real Docker probe, is_inlineable checked before/after adding
        /// a WITH clause to an otherwise-identical function body): a CTE anywhere in a scalar UDF's
        /// body blocks FROID inlining. Matches the public, documented "CTE" reason in
        /// sys.dm_xe_map_values('scalar_udf_inlining_blocked_reasons') by name.
        /// </summary>
        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.WithCtesAndXmlNamespaces is not null)
            {
                Report("CTE (WITH clause)");
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Oracle-confirmed 2026-08-17 (real Docker probe, is_inlineable checked directly): a
        /// `SELECT @v = expr(@v) FROM t` running-accumulator assignment - the string-concatenation-
        /// aggregate idiom real code uses in place of STRING_AGG/FOR XML PATH - is not inlined, while
        /// the identical shape assigning a value that does not reference the variable's own prior
        /// value (`SELECT @v = expr FROM t`) is. Only checked when the query specification has a
        /// FROM clause - a FROM-less `SELECT @v = @v + 1` is a plain scalar reassignment, a
        /// different, unprobed shape this scanner does not claim anything about.
        /// </summary>
        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause is not null)
            {
                foreach (var element in node.SelectElements)
                {
                    if (element is SelectSetVariable { Expression: { } expression } setVariable
                        && ReferencesVariable(expression, setVariable.Variable.Name))
                    {
                        Report("SELECT accumulator assignment reading its own variable");
                        break;
                    }
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget)
            {
                var qualifiedName = SchemaObjectNameHelper.QualifyFunctionCall(node);
                if (string.Equals(qualifiedName, ownQualifiedName, StringComparison.OrdinalIgnoreCase))
                {
                    Report("recursive self-reference");
                }
                else if (catalog.TryGetScalarUdfInfo(qualifiedName, out var calleeInfo)
                    && calleeInfo is { InlineabilityBlocker: { Length: > 0 } } or { EngineIsInlineable: false })
                {
                    Report($"references non-inlineable UDF {qualifiedName}");
                }
            }
            else if (node.FunctionName is { Value: { } functionName } && TimeDependentIntrinsics.Contains(functionName))
            {
                Report($"time-dependent intrinsic {functionName.ToUpperInvariant()}()");
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            if (string.Equals(node.Name, "@@DBTS", StringComparison.OrdinalIgnoreCase))
            {
                Report("@@DBTS");
            }

            base.ExplicitVisit(node);
        }

        private static bool ReferencesVariable(ScalarExpression expression, string variableName)
        {
            var finder = new VariableReferenceFinder(variableName);
            expression.Accept(finder);
            return finder.Found;
        }

        private sealed class VariableReferenceFinder(string variableName) : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(VariableReference node)
            {
                if (string.Equals(node.Name, variableName, StringComparison.OrdinalIgnoreCase))
                {
                    Found = true;
                }

                base.ExplicitVisit(node);
            }
        }

        private void Report(string reason) => Blocker ??= reason;
    }
}
