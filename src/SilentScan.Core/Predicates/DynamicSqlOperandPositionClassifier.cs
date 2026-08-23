using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public enum DynamicSqlOperandPosition
{
    Value,

    Identifier,

    Ambiguous,
}

public static class DynamicSqlOperandPositionClassifier
{
    public static DynamicSqlOperandPosition Classify(TSqlFragment root, int offset)
    {
        var literalVisitor = new LiteralSpanVisitor(offset);
        root.Accept(literalVisitor);

        var identifierVisitor = new IdentifierSpanVisitor(offset);
        root.Accept(identifierVisitor);

        if (literalVisitor.Best is { } literal && (identifierVisitor.Best is not { } identifier || literal.FragmentLength <= identifier.FragmentLength))
        {
            return DynamicSqlOperandPosition.Value;
        }

        return identifierVisitor.Best is not null ? DynamicSqlOperandPosition.Identifier : DynamicSqlOperandPosition.Ambiguous;
    }

    private static bool Contains(TSqlFragment node, int offset) =>
        node.StartOffset <= offset && offset < node.StartOffset + node.FragmentLength;

    private sealed class LiteralSpanVisitor(int offset) : TSqlFragmentVisitor
    {
        public Literal? Best { get; private set; }

        private void Consider(Literal node)
        {
            if (Contains(node, offset) && (Best is null || node.FragmentLength < Best.FragmentLength))
            {
                Best = node;
            }
        }

        public override void ExplicitVisit(IdentifierLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(IntegerLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(NumericLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(RealLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(MoneyLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(BinaryLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(StringLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(NullLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(DefaultLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(MaxLiteral node) { Consider(node); base.ExplicitVisit(node); }

        public override void ExplicitVisit(OdbcLiteral node) { Consider(node); base.ExplicitVisit(node); }
    }

    private sealed class IdentifierSpanVisitor(int offset) : TSqlFragmentVisitor
    {
        public Identifier? Best { get; private set; }

        public override void ExplicitVisit(Identifier node)
        {
            if (Contains(node, offset) && (Best is null || node.FragmentLength < Best.FragmentLength))
            {
                Best = node;
            }

            base.ExplicitVisit(node);
        }
    }
}
