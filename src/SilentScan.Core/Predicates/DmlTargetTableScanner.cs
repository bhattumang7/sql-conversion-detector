using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class DmlTargetTableScanner
{
    public static IReadOnlySet<string> Scan(IEnumerable<SqlParseResult> parseResults, DatabaseCatalog catalog)
    {
        var targets = new HashSet<string>(catalog.IdentifierComparer);
        foreach (var parseResult in parseResults)
        {
            var visitor = new Visitor(catalog, targets);
            parseResult.Fragment.Accept(visitor);
        }

        return targets;
    }

    private sealed class Visitor(DatabaseCatalog catalog, HashSet<string> targets) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(InsertStatement node)
        {
            RecordWrite(node.InsertSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            RecordWrite(node.UpdateSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            RecordWrite(node.DeleteSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            RecordWrite(node.MergeSpecification.Target, node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
        }

        private void RecordWrite(TableReference? target, WithCtesAndXmlNamespaces? withCtes)
        {
            if (DmlWriteTargetResolver.TryResolve(target, withCtes, catalog) is { } qualifiedName)
            {
                targets.Add(qualifiedName);
            }
        }
    }
}
