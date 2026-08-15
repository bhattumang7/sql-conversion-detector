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
    public static string? FindBlocker(StatementList? body, string ownQualifiedName, DatabaseCatalog catalog)
    {
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

        private void Report(string reason) => Blocker ??= reason;
    }
}
