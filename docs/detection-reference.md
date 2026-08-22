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

- **`xml` and legacy large-object (`text`/`ntext`/`image`) columns raise
  distinct, shape-dependent compile errors outside `IS NULL`/`IS NOT NULL` -
  never a single blanket message.** Oracle-confirmed directly (Docker,
  compat 160): a comparison operator (`=`/`<>`/`<`/`>`/`<=`/`>=`), `IN`,
  `BETWEEN`, or `NULLIF` against two `xml` columns raises Msg 305 ("The XML
  data type cannot be compared or sorted, except when using the IS NULL
  operator"); the identical shapes against `text`/`ntext`/`image` raise Msg
  402 ("The data types ... are incompatible in the ... operator") instead -
  `xml`'s own Msg 305 only appears for `text`/`ntext`/`image` in `ORDER
  BY`/`GROUP BY` (there it is Msg 306, wording nearly identical to Msg 305
  but naming `LIKE` as a second exception `xml` doesn't get). `xml` compared
  against a differently-typed, resolved operand (a string literal) raises
  Msg 402 too, not Msg 305 - the blanket message is reserved for same-
  category-shaped comparisons. `SELECT DISTINCT` over either family raises a
  third message, Msg 421 ("... cannot be selected as DISTINCT because it is
  not comparable"). `LIKE` against `text`/`ntext` compiles and runs
  normally (the engine's own Msg 306 text names it as the second exception);
  there is no equivalent exception for `xml`.

- **A `CASE`/`COALESCE` branch over an `xml`/legacy-large-object column never
  raises a comparability error - only `NULLIF` does.** Oracle-confirmed:
  `CASE WHEN ... THEN XmlCol ELSE XmlCol2 END` and `COALESCE(XmlCol,
  XmlCol2)` both compile and run, because neither construct compares its
  branches against each other (it picks one). `NULLIF(XmlCol, XmlCol2)`
  does compare its two arguments internally and fails with the same Msg
  305/402 the other comparison shapes get. A rule flagging "operand not
  comparable" in a `CASE`/`COALESCE` branch would be a false positive; do
  not re-propose it.

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

## Predicate survival (normalization/simplification)

Scope note for `detection-tasklist.md`'s top open item. Every predicate
scanner in `src/SilentScan.Core/Predicates/` reads a `WHERE`/`HAVING`/`ON`
tree exactly as parsed and decides sargability leaf-by-leaf; none of them
ask whether the engine's own normalize/simplify pass (bind → derive type →
**normalize/simplify** → sargability → plan) would rewrite or eliminate that
leaf before sargability is ever evaluated on it. `LiteralComparisonFolder`
is the only existing normalization-shaped code in the tree, and it is
deliberately narrow by its own doc comment: literal-vs-literal only, no
AND/OR propagation, NULL excluded outright. Nothing composes it across a
boolean tree.

### Boolean-tree shapes that need contradiction/tautology detection

Three-valued logic (`TRUE`/`FALSE`/`UNKNOWN`) is ANSI SQL, not an
engine-specific fact, and governs every shape below: `UNKNOWN AND TRUE =
UNKNOWN`, `UNKNOWN AND FALSE = FALSE`, `UNKNOWN OR TRUE = TRUE`, `UNKNOWN OR
FALSE = UNKNOWN`, `NOT UNKNOWN = UNKNOWN`. A `WHERE`/`ON` clause keeps a row
only when the whole condition evaluates `TRUE` - `UNKNOWN` is treated exactly
like `FALSE` at the top level, but NOT inside a nested AND/OR, where it
propagates per the table above instead of collapsing early. Any contradiction
checker has to reason in three values, not two, or it will misclassify the
NULL case.

1. **Same-column AND contradiction, non-nullable-safe only.** `x = 1 AND x =
   2` is unconditionally `FALSE` regardless of whether `x` is NULL (`NULL =
   1` is `UNKNOWN`, `UNKNOWN AND anything-but-TRUE` is never `TRUE`) - safe
   to fold without a nullability check. Range contradictions (`x > 5 AND x <
   3`) fold the same way. This is the highest-confidence, lowest-effort
   shape: pure literal-bound reasoning per column, no NULL case to get
   wrong.
2. **OR tautology - NULL-unsafe, the shape to get right first.** `x = 1 OR x
   <> 1` looks like a tautology but is NOT one: when `x IS NULL`, both sides
   evaluate `UNKNOWN`, so the whole OR is `UNKNOWN`, not `TRUE`, and the row
   is excluded. A tautology fold is only safe when the column is provably
   non-nullable (from the catalog) or when an explicit `x IS NULL` branch is
   already OR'd in alongside the complementary-literal branches. Folding this
   wrong is a false negative in the OPPOSITE direction from the rest of this
   project's precision bias would predict: declining to fold is always safe
   here, folding without the nullability check is the unsafe direction.
3. **`IS NULL`/`IS NOT NULL` interaction with a same-column AND.** `x = 1
   AND x IS NULL` is unconditionally `FALSE` the same way shape 1 is (`x = 1`
   is `FALSE` or `UNKNOWN`, never `TRUE`, whenever `x IS NULL` is `TRUE`) -
   safe to fold without a nullability check, same confidence tier as shape 1.
4. **Redundant-branch absorption inside an already-flattened AND.** `x = 1
   AND (x = 1 OR y = 2)` - the inner OR is redundant given the outer
   conjunct, but the outer `x = 1` itself is untouched and still reaches
   sargability normally. This matters to `CatchAllPredicateScanner`: an
   equivalent outer equality absorbs its narrow `(Col = @p OR @p IS NULL)`
   idiom, so the latter no longer causes a scan. The normalizer marks only
   the inner disjunction eliminated, using exact supported scalar identity
   and the equality's commutative form; the outer conjunct remains live. Static and
   execution-plan tests confirm the resulting equality seeks.
5. **Subquery flattening changing a predicate's effective scope.** A
   correlated `EXISTS`/`IN`/scalar subquery predicate can be flattened by the
   engine into a join, at which point a condition that looks like it lives
   "inside the subquery" for AST-scoping purposes is evaluated at the outer
   query's row source instead. Audited after the normalization module shipped:
   `TypedPredicateExtractor` and `NonSargablePredicateScanner` already enter
   every nested `QuerySpecification` with a fresh FROM scope and a fresh
   condition-local normalization set, so no finding crosses the subquery
   boundary or relies on the flattened placement. `NotInNullableSubqueryScanner`
   separately resolves its correlated-NULL check in the subquery's native
   scope. No shipped rule needs a flattening-specific suppression.
6. **Constant folding across a function/arithmetic wrap.** `x + 0 = 5` is
   algebraically foldable to `x = 5`, which WOULD be sargable - but nothing
   in the engine's normalize/simplify pass has been confirmed (oracle or
   otherwise) to actually perform this specific rewrite for an *indexed*
   column before cost-based optimization. The dedicated oracle probe now
   confirms `x + 0 = 5` does not seek an indexed integer column, so the
   existing arithmetic-wrap finding remains correct and no suppression is
   warranted. `LiteralComparisonFolder` remains deliberately limited to
   literal-vs-literal arithmetic.

### Shipped rules that assume a predicate reaches the optimizer as written

Every one of these treats each `FlattenAnd`-split leaf (or, for
`TypedPredicateExtractor`, every `BooleanComparisonExpression` node reached
by a generic tree walk with no AND/OR awareness at all) as independently
sargability-relevant, with no check for whether shapes 1-3 above would
eliminate that leaf, or the branch it lives in, before the optimizer ever
scores it:

- `TypedPredicateExtractor` (`src/SilentScan.Core/Predicates/TypedPredicateExtractor.cs:349`,
  `ExplicitVisit(WhereClause)`) - the shared per-predicate typed-comparison
  feed every sargability/conversion finding stream is built from. Highest
  blast radius: a contradiction-eliminated leaf here propagates into every
  downstream consumer, not just one rule.
- `NonSargablePredicateScanner` (`src/SilentScan.Core/Predicates/NonSargablePredicateScanner.cs:200`,
  `ExplicitVisit(WhereClause)`) - same generic-walk shape as above, same
  exposure.
- `CatchAllPredicateScanner` (`src/SilentScan.Core/Predicates/CatchAllPredicateScanner.cs:193`
  `FlattenOr`/`InspectOrClause`) - already OR-aware for its own narrow
  `(Col = @p OR @p IS NULL)` idiom, but has no general OR-tautology check
  (shape 2); a hand-written `x = @p OR x <> @p`-shaped guard elsewhere in the
  same clause would not be recognized as dead.
- `DuplicationScanner` (`src/SilentScan.Core/Predicates/DuplicationScanner.cs:834`,
  local `FlattenAnd`) - already calls `LiteralComparisonFolder` for its own
  "always true/false literal comparison" check per its doc comment, but only
  literal-vs-literal, never column-vs-literal same-column contradiction
  (shape 1) across the flattened set.
- `PartialCompositeForeignKeyJoinScanner` (`src/SilentScan.Core/Predicates/PartialCompositeForeignKeyJoinScanner.cs:146-188`) -
  flattens a JOIN's `ON` and the statement's `WHERE` into one leaf set
  without checking whether a `WHERE`-side contradiction (shape 1 or 3) makes
  the whole branch dead, which would make a "partial composite key join"
  finding derived from it moot.
- `QueryAntiPatternScanner` (`src/SilentScan.Core/Predicates/QueryAntiPatternScanner.cs:1307,1383`) -
  same exposure for its `HAVING`- and join-column-derived checks.
- `JoinKeyUniqueness` / `NotInNullableSubqueryScanner` (`JoinKeyUniqueness.cs:35`,
  `NotInNullableSubqueryScanner.cs:94,148`) - lower priority: both already
  reason narrowly about a specific idiom (join-key uniqueness, `NOT IN`
  correlated-NULL), so a stray same-column contradiction elsewhere in the
  same clause is less likely to change their verdict, but not confirmed
  immune.

## Halloween Protection bypasses for self-referencing DML

`SelfReferencingDmlRuleId`'s scanner excludes any INSERT/UPDATE/DELETE/MERGE
whose own TOP row limiter is the literal integer 1 (not `PERCENT`, not a
variable/parameter): oracle-confirmed, via compile-only `SET SHOWPLAN_XML`
probes across all four statement kinds and cross-checked against otherwise-
identical TOP(2)/TOP(1) PERCENT controls, that a literal TOP(1) drops the
Eager Spool/Sort entirely - the guaranteed-at-most-one-row cardinality alone
satisfies Halloween Protection.

A second bypass exists in the engine (an internal nest-ID-based tracking mode
that can skip the spool independent of TOP), but its real gating conditions
are execution-context internals - not a simple compatibility-level or catalog
property check - and empirical probes against ordinary top-level self-
referencing DML on a plain disk-based, non-FILESTREAM, non-replicated table
never triggered it: the Eager Spool/Sort still appeared in every case tried
outside the literal-TOP(1) shape above. Not implemented - no catalog-decidable
condition for it was found precise enough to gate on.
