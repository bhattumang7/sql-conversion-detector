# Detection reference

Facts about SQL Server's own behaviour that can't be re-derived from our code,
and decisions that would otherwise be re-proposed. Not a plan — see
`detection-tasklist.md` for open work. Every fact below is engine behaviour,
confirmed independently of any one tool's output.

## Type comparability and implicit conversion

- **General comparability (can two types be compared at all, independent of
  category).** The engine excludes exactly `text`, `ntext`, `image`,
  `xml`, and CLR user-defined types from general comparability; every other
  base type - including `sql_variant` - is comparable. This is precisely
  `VerdictClassifier.IsOutOfModelCategory`'s exclusion list, minus
  `SqlVariant`: `sql_variant` is comparable in general (its actual
  comparison semantics depend on the boxed runtime value, which is why it
  still can't carry a static verdict), matching the deliberate carve-out in
  `VerdictClassifier.ClassifyWithReason` that lets `sql_variant` flow through
  the normal precedence rule instead of a blanket Unknown.

- **Dominant-type selection for CASE/COALESCE/IIF and set operations.** When
  two operand types differ, the higher-precedence side wins outright *except*
  for three pairs that are the "same underlying storage family, fixed vs.
  variable length" - `char`/`varchar`, `nchar`/`nvarchar`,
  `binary`/`varbinary`. For those three pairs specifically, the
  variable-length member always wins over the fixed-length one, independent
  of operand order. This is exactly `SqlTypeCategory`'s ordinal order
  (`VarChar` > `Char`, `NVarChar` > `NChar`, `VarBinary` > `Binary`) and
  exactly what `ExpressionTypeInferencer.Combine` already implements.

- **A compile-time-constant CASE/IIF condition (e.g. `WHEN 1=1`) is folded
  before dominant-type selection runs**, so the untaken branch's type never
  enters the merge - the result is just the taken branch's own declared
  type. This looks like a different, "position wins" merge rule if a probe
  uses a constant condition; it is not a distinct type rule, it is dead-code
  elimination happening first. A genuinely non-constant condition (a real
  column predicate) always goes through the ordinary dominant-type merge
  above, regardless of whether the operands are columns or variables.

- **Sargability of a predicate that requires CONVERT_IMPLICIT splits into two
  independent mechanisms**, matching `VerdictClassifier`'s own split:
  - Cross-category conversion (e.g. `varchar` column vs. an `int` value)
    goes through the engine's "dynamic range through convert" path -
    `RangeSeek` when available, never for a same-category length/MAX
    mismatch.
  - Same-category, MAX-vs-bounded-length mismatch (e.g. `varchar(20)`
    column vs. a `varchar(MAX)` value) goes through a *different*,
    dedicated "range with mismatched types" path - also capable of
    `RangeSeek`, selected independently of the cross-category path above.
  Both paths are real, independent constructs in the optimizer, not one
  mechanism with two callers - which is why `VerdictClassifier`'s remarks
  are correct to describe them as genuinely different branches rather than
  variations of the same rule.

- **A real-data probe's plan CHOICE for the dynamic-range-through-convert path
  is a cardinality-driven fact, not a structural one, and must not be
  confused with the other.** A same-table WHERE-clause column-vs-column
  comparison, probed with 5,000 real rows and real statistics, shows the
  optimizer choosing a plain scan over the dynamic range seek a JOIN
  predicate on the identical type pair keeps. That looks like a structural
  "needs an outer row source" rule, but it isn't one: this project's own
  DDL-only oracle harness (no data at all) shows `GetRangeThroughConvert`
  compiled into the plan for the same-table shape too - the construct is
  structurally available either way, and real data merely changes whether
  the optimizer's cost model prefers it, exactly the cardinality-dependence
  CLAUDE.md's own rule excludes from a static verdict. Column-vs-column -
  same table or joined - correctly keeps the matrix's plain
  column-vs-variable-probed answer; do not special-case it again from a
  real-data probe without re-confirming against the DDL-only harness first.

- **A narrow (non-unicode) column longer than 4000 characters compared
  against a unicode MAX value/column loses the seek entirely
  (`Clustered Index Scan`), even under a Windows collation where the same
  category pair at a bounded length gets a genuine dynamic-range seek** -
  a length-triggered promotion, not just the plain cross-category case
  `TypePairMatrix` already covers. Single-probe oracle-confirmed (5,000-row
  real data, Windows collation, `varchar(4001)` vs `nvarchar(MAX)`); the
  control at a bounded length (`varchar(10)`) seeks cleanly, so the
  divergence is attributable to the length threshold and not row count or
  statistics. `VerdictClassifier.IsLengthTriggeredUnicodePromotion` encodes
  this ahead of the matrix lookup. Not yet swept across collation families
  or the reverse operand order - only the single probed shape is confirmed.

- **The `json` type (native, SQL Server 2025+, not the `NVARCHAR`-backed
  legacy pattern) is stricter than `xml`: it is not comparable at all, not
  even against a bare `NULL` literal.** Oracle-confirmed directly (SQL Server
  2025, `RTM-CU8`): `json = json`, `json = '{}'`, and `json = NULL` (as
  opposed to `IS NULL`) all raise Msg 13636, "The JSON data type cannot be
  compared or sorted, except when using the IS NULL operator." `IS NULL`/
  `IS NOT NULL` work normally. Routed through `SqlTypeCategory.Json` and
  `VerdictClassifier.IsOutOfModelCategory`, same as `xml`.

## Sargability and index eligibility

- **The engine's comparison-operator sargability gate treats every non-bare-column operand
  identically - there is no per-wrapper-type unwrapping.** A column wrapped in a function call,
  CAST/CONVERT, or arithmetic is rejected from range-seek eligibility by the same single check
  that requires an operand to be a bare column reference; none of these wrapper shapes gets any
  special-cased handling that would let it through. This confirms there is no case where the
  engine quietly rescues a function-wrapped/CAST/arithmetic-wrapped predicate into a seek - the
  loss is unconditional, matching every one of this project's own "wrapping a column blocks the
  seek" rules with no exception to account for.

- **`LIKE` sargability is decided by a dedicated range-transform step, separate from the general
  comparison-operator gate above, and is stricter than "can't rule out a leading wildcard."** It
  requires the pattern operand to be a literal constant node before any wildcard analysis even
  runs - a variable/parameter pattern is rejected outright, not merely treated as unprovable. Only
  after that does it inspect the literal text for a disqualifying wildcard; the sole exception is
  the single-character pattern `%` alone, which builds a degenerate not-NULL predicate rather than
  a range. Every other wildcard-containing literal pattern, and every non-literal pattern, yields
  no seekable range.

- **An expression-derived column (a view/derived-table/TVF SELECT-list expression referenced
  downstream) can never reach the bare-column-operand sargability gate above, because view/TVF
  expansion never substitutes a computed expression back down to a raw base column.** Expansion
  only ever adds the base table into the query tree; a reference to a computed output column binds
  to the expression itself. There is no optimizer rewrite that collapses a CAST/expression column
  back into a plain column reference, so this loss is permanent for the lifetime of the query tree,
  not merely "not yet proven seekable."

- **The catch-all `(Col = @p OR @p IS NULL)` optional-filter idiom cannot be rescued by the
  engine's own OR-to-seek rewrite (index union).** Index union is real and can turn some
  disjunctions into a seek-based plan, but it requires every branch of the OR to be a
  column-referencing comparison predicate; an `IS NULL` test against a parameter/variable tests
  the parameter's own value, not a column, and structurally disqualifies the whole disjunction from
  the gate before index union is even considered. The "one cached plan must suit every NULL/
  non-NULL state" cost is therefore structural, not merely a missed optimization opportunity.

- **Two differing column collations (not a literal-vs-column comparison, which coerces silently
  and for free) have no transparent rescue path - the engine either hard-fails compilation (Msg
  468, "Cannot resolve the collation conflict") once a coercibility algorithm run over both
  operands can't determine a winning collation, or proceeds to an implicit conversion.** This
  applies identically whether the two columns are directly compared or joined across a foreign
  key relationship with drifted declared collations - there is no case where the engine silently
  and correctly reconciles two genuinely different column collations at zero cost.

- **`TEXT`/`NTEXT`/`IMAGE` (legacy large-object types) can never appear in any index at all - not
  just as a key column (the same restriction MAX-typed `VARCHAR(MAX)`/`NVARCHAR(MAX)`/
  `VARBINARY(MAX)` columns already carry), but not even as a nonclustered index's INCLUDE column,
  where a MAX-typed column IS accepted.** Oracle-confirmed directly (Docker SQL Server 2022): `CREATE
  INDEX ... (col)` on a TEXT/NTEXT/IMAGE column raises Msg 1919 ("is of a type that is invalid for
  use as a key column in an index"), identical to a MAX-typed column; `CREATE INDEX ... INCLUDE
  (col)` on the same column raises a second, distinct error, Msg 1999 ("is of a type that is
  invalid for use as included column in an index") - confirmed a MAX-typed column does NOT raise
  this second error, so the two type families carry genuinely different, non-overlapping
  restrictions, not variants of the same fact.

- **A filtered index/indexed view's actual required-SET-options list, read from the engine's own
  compiled-in message text, has seven members: `ANSI_NULLS`, `ANSI_WARNINGS`, `ANSI_PADDING`,
  `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL` (all required ON), plus `QUOTED_IDENTIFIER` (required ON)
  and `NUMERIC_ROUNDABORT` (required OFF), gated by a separate check.** Do not treat the
  seven-option list as uniformly reproducible on its own, though: `ARITHABORT OFF` alone was
  oracle-probed directly (real seeded data, a real filtered index AND a real indexed view) and
  demonstrably changed neither plan at all on this engine version/edition - the compiled-in
  message text names a broader set than this build's optimizer actually enforces for that one
  option, so it was deliberately NOT shipped as a rule despite appearing in the required list.
  `ANSI_PADDING OFF` alone, by contrast, WAS oracle-confirmed directly to degrade a filtered-index
  seek to a full clustered index scan (all other options left at their default) - the engine's
  message text and the observed plan behavior agree for this option, unlike ARITHABORT. Always
  oracle-probe each option's real plan effect individually before shipping it; the compiled-in
  required-options message is a candidate list to test against, not a source of truth on its own.

- **`TRY_CAST`/`TRY_CONVERT` are not a distinct expression-node type in the engine - they compile
  to the same node class as `CAST`/`CONVERT`, with one additional flag bit set at construction,
  layered on top of the ordinary CAST/CONVERT node shape.** This is structural evidence (not a
  directly-read single determinism-table entry) that `TRY_CAST` feeds the identical
  determinism-bitmask mechanism CAST's own DATEFORMAT-dependency classification uses, rather than
  being computed through some separate, potentially-more-lenient path - consistent with (not yet
  byte-level-proof of) the non-persisted-non-determinism premise this project's TRY_CAST-computed-
  column rule depends on.

- **`FOR SYSTEM_TIME` genuinely expands to a `UNION ALL` of the current-table and history-table
  branches** - read directly from the temporal-table transformation's own tree-construction
  sequence (two independently-bound branches, each wrapped in a Project, both passed to an
  explicit UNION ALL constructor). A current-side index with no structurally matching history-side
  index therefore degrades specifically to a full scan of the history-table branch, not some other
  access path - confirming the mechanism a shipped rule's cost claim depends on.

- **An `INDEX(...)` table hint hard-forces that single physical index with no cost-based
  fallback.** The forced-index id is read from a field stored directly on the access-path node
  itself, checked by a dedicated "is this index forced" predicate - not folded into the cost model
  as a strong preference the optimizer could still outbid. If the hinted index's own leading key
  column isn't bound, there is structurally no other access path available to that node.

- **`XML` parses to its own dedicated ScriptDom node
  (`XmlDataTypeReference`), never a `SqlDataTypeReference`.** Before this was
  handled explicitly, an XML column's type resolved to `null` (the same path
  `CURSOR`/`TABLE`/CLR-UDT types take) rather than `SqlTypeCategory.Xml` -
  same eventual Unknown verdict either way, but via the generic
  "operand-type-unresolved" reason instead of the more specific
  "out-of-model-category:Xml" one, and any future logic keyed on the actual
  category (rather than just null-checking) would have silently never seen
  Xml at all.
