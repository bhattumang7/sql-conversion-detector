namespace SilentScan.Core.Predicates;

/// <summary>One <c>INDEX(...)</c> table-hint claim (docs/detection-checklist.md "Hint and index-shape catalog checks": "Hint validity against the catalog").</summary>
public enum IndexHintFindingKind
{
    /// <summary>
    /// The hint names an index that does not exist anywhere in the catalog for this table -
    /// oracle-confirmed (Docker instance, real seeded index) this is a hard compile error (Msg
    /// 308, "Index '...' on table '...' (specified in the FROM clause) does not exist"), not a
    /// silently-ignored hint. Shipped anyway, the same "names a provably-always-failing query
    /// with more precision than the raw engine message" reasoning <see
    /// cref="TempTableExecShapeFindingKind.ColumnCountMismatch"/> already applied for an
    /// identical guaranteed-error case: this pass can name the exact broken hint statically,
    /// before anything runs, across an entire corpus at once - real value even though the engine
    /// itself would refuse the query the moment it actually executes. A dropped/renamed index
    /// left behind by a migration that forgot to update every hint site is the realistic cause.
    /// </summary>
    IndexDoesNotExist,

    /// <summary>
    /// The hint names a REAL index, but that index's own leading key column is not referenced
    /// anywhere in the statement at all - the hint forces the engine to use this specific index
    /// (T-SQL's own documented `INDEX` table-hint semantics: it does not merely suggest, it
    /// requires), and with no bound on the leading key the engine cannot descend the index's own
    /// B-tree to a useful starting point, degrading what would otherwise be a normal access path
    /// into a full index scan. Oracle-confirmed directly (Docker instance, real seeded index, `SET
    /// SHOWPLAN_XML`): the identical query without the hint produces a clean `Clustered Index
    /// Seek`; adding `WITH (INDEX(IX_NonLeadingColumn))` against a predicate that never touches
    /// that index's leading column degrades the SAME query to `Index Scan` + `Nested Loops`
    /// (bookmark lookup back to the real access path); hinting an index whose leading column IS
    /// bound by the query's own predicate stays a clean `Index Seek` even through the hint,
    /// confirming the leading-column binding, not the hint itself, is what decides seek vs. scan.
    /// Shares its "is the leading column bound anywhere" check with <see
    /// cref="CompositeIndexLeadingColumnFinding"/> (deliberately the SAME conservative,
    /// liberal-to-suppress test, generalized to single-column indexes too - a hint forcing a
    /// single-column index with its lone key column unbound anywhere degrades identically).
    /// </summary>
    HintedIndexNotSeekable,
}

/// <summary>
/// See <see cref="IndexHintFindingKind"/> for the mechanism behind each kind.
///
/// Known v1 scope limits, stated honestly: only the identifier form of the modern `WITH
/// (INDEX(IndexName))`/`WITH (INDEX(Name1, Name2))` table hint is inspected - the ordinal form
/// (`INDEX(0)`/`INDEX(1)`, referring to the heap/clustered index by position rather than by name)
/// has no catalog name to validate or resolve against a leading column the same way, and is
/// deliberately out of scope rather than guessed at. `FORCESEEK`'s own optional index argument
/// (`ForceSeekTableHint`, a distinct ScriptDom node from `IndexTableHint`) and `FORCESCAN` are
/// related hint syntaxes with a similar "forces a specific access path" story, also deliberately
/// out of v1 scope - a real, documented gap, not a silent omission.
/// </summary>
public sealed record IndexHintFinding(
    IndexHintFindingKind Kind,
    string TableQualifiedName,
    string HintedIndexName,
    string? LeadingColumnName,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
