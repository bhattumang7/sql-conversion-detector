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

    /// <summary>The <c>&lt;expr&gt;.method(...)</c>-shaped XML data-type instance methods reached via <see cref="FunctionCall"/> - <c>.nodes()</c>/<c>.modify()</c> are separate ScriptDom node shapes, handled by their own visitor overrides.</summary>
    private static readonly HashSet<string> XmlInstanceMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "query", "exist",
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
        /// Oracle-confirmed 2026-08-20 (real Docker probe): a scalar UDF body querying a `sys.*`
        /// catalog view/table blocks inlining, matching the documented "SystemDataAccess" reason -
        /// calling a system FUNCTION (e.g. SUSER_SNAME()) alone does not, isolated separately, so
        /// this is scoped to catalog TABLE access specifically, not any system-prefixed construct.
        /// </summary>
        public override void ExplicitVisit(NamedTableReference node)
        {
            if (string.Equals(node.SchemaObject.SchemaIdentifier?.Value, "sys", StringComparison.OrdinalIgnoreCase))
            {
                Report("system catalog access (sys." + node.SchemaObject.BaseIdentifier.Value + ")");
            }

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

            // Oracle-confirmed 2026-08-20 (real Docker probe, otherwise-identical bodies): a
            // `SELECT ... ORDER BY ...` with no TOP blocks inlining; the identical query with
            // TOP N added inlines cleanly. Matches the public, documented "OrderByWithoutTop"
            // reason by name. Checked as bare presence/absence, matching exactly what was
            // verified - a TOP N/TOP N PERCENT variant's own effect was not separately probed,
            // so this does not special-case one.
            if (node.OrderByClause is not null && node.TopRowFilter is null)
            {
                Report("ORDER BY without TOP");
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
            else if (node.FunctionName is { Value: { } aggName } && string.Equals(aggName, "STRING_AGG", StringComparison.OrdinalIgnoreCase))
            {
                // Oracle-confirmed 2026-08-20 (real Docker probe): a plain, non-accumulating
                // `SELECT @r = STRING_AGG(...) FROM t` (not reading @r's own prior value - that
                // shape is the separate AggregatingAssignment check above) still blocks inlining.
                // Matches the documented "StringAggFunc" reason by name - distinct from ordinary
                // aggregates (SUM/COUNT/AVG), which do not block on their own.
                Report("STRING_AGG()");
            }
            else if (node.CallTarget is ExpressionCallTarget
                && node.FunctionName is { Value: { } xmlMethodName } && XmlInstanceMethods.Contains(xmlMethodName))
            {
                // Oracle-confirmed 2026-08-20 (real Docker probe, all three tested individually):
                // an XML data-type instance method call blocks inlining - declaring an XML-typed
                // variable alone does not (isolated separately). CallTarget is ExpressionCallTarget
                // for any `<expr>.method(...)` shape, not XML-specific by itself, but "value"/
                // "query"/"exist" are not real method names on any other built-in instance-method
                // type (hierarchyid/geometry/geography all use differently-named methods), so this
                // scanner does not need local variable type tracking (which it has none of today)
                // to keep this precise.
                Report($"XML data-type method .{xmlMethodName}()");
            }
            else if (node.FunctionName is { Value: { } functionName } && TimeDependentIntrinsics.Contains(functionName))
            {
                Report($"time-dependent intrinsic {functionName.ToUpperInvariant()}()");
            }

            base.ExplicitVisit(node);
        }

        /// <summary>Oracle-confirmed 2026-08-20: <c>FROM @doc.nodes(...)</c> XML shredding blocks inlining, same family as the other XML instance methods above.</summary>
        public override void ExplicitVisit(VariableMethodCallTableReference node)
        {
            Report("XML data-type method .nodes()");
            base.ExplicitVisit(node);
        }

        /// <summary>Oracle-confirmed 2026-08-20: <c>SET @doc.modify(...)</c> blocks inlining, same family as the other XML instance methods above - FunctionCallExists distinguishes this method-call form from a plain <c>SET @v = expr</c>.</summary>
        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.FunctionCallExists && string.Equals(node.Identifier?.Value, "modify", StringComparison.OrdinalIgnoreCase))
            {
                Report("XML data-type method .modify()");
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
