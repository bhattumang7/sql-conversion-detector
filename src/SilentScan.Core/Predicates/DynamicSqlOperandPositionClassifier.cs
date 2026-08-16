using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

/// <summary>What grammar role a position inside a reparsed T-SQL fragment plays.</summary>
public enum DynamicSqlOperandPosition
{
    /// <summary>A scalar VALUE position - comparison/IN/LIKE/BETWEEN operand, VALUES-row scalar, assignment RHS, function argument, or any other position a <c>Literal</c> can legally occupy.</summary>
    Value,

    /// <summary>A NAME position - a schema object, column, or alias identifier part.</summary>
    Identifier,

    /// <summary>Neither could be determined (the offset lands on a keyword, operator, or punctuation) - declined, never guessed.</summary>
    Ambiguous,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality" - classifies a position inside a
/// reparsed dynamic-SQL fragment as a VALUE or an IDENTIFIER, so the pipeline can tell "a value
/// was concatenated into this constant SQL string" apart from "a table/column name was" (a
/// concatenated identifier is often a legitimate, unavoidable pattern - dynamic column/table
/// selection - while a concatenated value is exactly the plan-cache-pollution antipattern this
/// stream targets).
///
/// ScriptDOM's grammar makes this a clean binary in practice, with no need for a full ancestor
/// walk through every intervening production: a <see cref="Literal"/> node can only ever appear
/// where a scalar value is expected (it has no other legal grammar position), and an
/// <see cref="Identifier"/> node can only ever appear where a name is expected - the two node
/// kinds never overlap in span. Finding the SMALLEST Literal or Identifier whose span contains the
/// offset (never both, but the smaller wins on the rare occasion ScriptDOM nests one construct's
/// span inside another's) answers the question directly. Neither found - <see
/// cref="DynamicSqlOperandPosition.Ambiguous"/>.
/// </summary>
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

    /// <summary>
    /// <see cref="Literal"/> itself is never dispatched to - every concrete literal type's own
    /// <c>Accept</c> override calls <c>ExplicitVisit</c> for ITS OWN concrete type at compile time
    /// (ScriptDOM's visitor pattern resolves the overload statically inside each node type's own
    /// <c>Accept</c>, not virtually against the visitor's most-derived applicable override), so a
    /// visitor overriding only the abstract base type's overload is never actually called for any
    /// real literal - confirmed empirically (every literal-position test failed silently as
    /// Ambiguous before this was found and fixed). Every one of ScriptDOM's 11 concrete <see
    /// cref="Literal"/> subclasses needs its own override.
    /// </summary>
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
