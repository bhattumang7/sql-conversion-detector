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

- **`ALTER TABLE ... ALTER COLUMN` between a char-family type
  (`char`/`varchar`/`nchar`/`nvarchar`) and `binary`/`varbinary` only fails
  in one direction, and length/collation differences on their own never
  fail.** Oracle-confirmed (standing Docker instance): retyping a char-
  family column directly to `binary`/`varbinary` raises Msg 257 ("Implicit
  conversion from data type ... is not allowed. Use the CONVERT function to
  run this query.") - there is no implicit conversion that way, and
  `ALTER COLUMN`'s own syntax has no way to carry an explicit
  `CONVERT`/`CAST` alongside the new type. The reverse direction
  (`binary`/`varbinary` retyped to a char-family type) always deploys - the
  implicit conversion exists that way. Independently, an `ALTER COLUMN`
  that changes only length, or only collation (same string family, e.g.
  `Latin1_General_CI_AS` to `Latin1_General_CS_AS`), always deploys too;
  neither is a real risk on its own. `AlterColumnSafetyScanner`'s
  `IncompatibleFamilyConversion` kind flags exactly the one failing
  direction.

- **`ALTER TABLE ... ALTER COLUMN` narrowing a `DECIMAL`/`NUMERIC` column's
  declared precision or scale below its current catalog value is a DDL-time
  risk decided by the actual stored data, not just the declared range.**
  Oracle-confirmed: if any existing row's whole-number part no longer fits
  the narrower precision, the `ALTER COLUMN` statement itself fails with Msg
  8115 ("Arithmetic overflow error converting numeric to data type
  numeric"). If every value fits, the statement succeeds silently and any
  digits past the new scale are rounded away with no warning. A
  `TIME`/`DATETIME2`/`DATETIMEOFFSET` column's fractional-seconds scale
  narrowing only ever takes the silent-rounding path - truncating digits
  past a time value's seconds can't overflow. `AlterColumnSafetyScanner`'s
  `PrecisionOrScaleNarrowing` kind flags the declared-type narrowing itself
  (source-text comparison, no data inspection), since either outcome is a
  real risk.

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

- **A selective XML index's own `CREATE SELECTIVE XML INDEX` never enforces the 900-byte key-length
  ceiling or the no-large-object restriction on a promoted path's declared `SQL_DATA_TYPE` - the
  check only fires later, at `CREATE XML INDEX ... USING XML INDEX ... FOR (path)` (a secondary
  selective XML index), because only then does the promoted path become an actual index key
  column.** Oracle-confirmed directly (Docker SQL Server 2025): a primary `CREATE SELECTIVE XML
  INDEX` with a promoted path declared `VARCHAR(MAX)` or `NVARCHAR(4000)` (well past 900 bytes)
  deploys with no error at all; building a secondary index over that same path then fails - Msg
  6391 ("is promoted to a type that is invalid for use as a key column in a secondary selective XML
  index") for the MAX-typed path, Msg 6395 ("The maximum key length is 900 bytes... has maximum
  length of N bytes") for any string type whose byte width exceeds 900 (`VARCHAR(901)`+,
  `NVARCHAR(451)`+ - the byte width doubles for the unicode types, matching `sys.columns.max_length`
  semantics). The boundary is exact: `VARCHAR(900)`/`NVARCHAR(450)` (900 bytes) deploy fine.
  Non-string promoted path types (`INT`, `BIGINT`, ...) never trigger either check.
  `VARBINARY`/`TEXT`/`NTEXT`/`DATETIME2` and other unsupported promoted-path types are rejected
  outright at the primary `CREATE SELECTIVE XML INDEX` statement itself (Msg 6375, "data type ...
  is not allowed") - a distinct, simpler, already-primary-time failure, out of scope for the
  value-column-width rule.

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

### CHECK-constraint/NOT-NULL predicate contradiction

`CheckConstraintPredicateContradictionRuleId`/`NotNullPredicateContradictionRuleId`
(`src/SilentScan.Core/Predicates/CheckConstraintPredicateContradictionScanner.cs`).
Oracle-confirmed directly (Docker SQL Server 2025, module-body compilation, not
ad-hoc batches): a `WHERE`/(UPDATE/DELETE) predicate that literal-compares a
column against a value provably outside a trusted, enabled, single-column
numeric `CHECK` constraint's own `AND`/`OR`/`BETWEEN`-built interval compiles
to a bare `Constant Scan` - the same plan shape as a literal `WHERE 1 = 0` -
confirmed for a single comparison, an `AND`-combined range, an `OR`-combined
domain, a `BETWEEN`-shaped query predicate, an `OR`-of-two-disjoint-ranges
query predicate, and a nullable column carrying a trusted CHECK (the fold
doesn't require `NOT NULL`, since a three-valued-logic comparison already
excludes NULL rows independent of the constraint). The identical fold also
happens for `IS NULL` against a column the catalog declares `NOT NULL`,
independent of any CHECK constraint. Confirmed NOT to fold - and the scanner
never fires - when: the CHECK constraint is `NOT TRUSTED` (`WITH NOCHECK`,
never revalidated - `sys.check_constraints.is_not_trusted`); the query
predicate compares against a session variable/parameter rather than a
literal (unknown at compile time to the module, unlike ad-hoc-batch simple
parameterization which is a separate, session-level effect this scanner
doesn't need to reason about since it only reads module source text); or an
`OR`'s other branch doesn't itself contradict (three-valued `OR` semantics -
only proven when every disjunct is independently proven unsatisfiable, exactly
mirroring the existing `PredicateSurvivalAnalyzer.Classify` `OR` rule).

Scope narrowed deliberately to keep every finding inside the exact shape
oracle-confirmed above: only single-column `CHECK` constraints built purely
from `AND`/`OR`/`BETWEEN` over column-vs-numeric-literal comparisons are
folded into a trusted interval (reusing `NumericValueRangeSet`, the same
interval algebra `PredicateSurvivalAnalyzer` already uses for its own
same-predicate contradiction detection). A `CHECK` constraint that spans more
than one column, compares against a string/date literal, calls a function, or
uses an `IN` list contributes no interval and is silently skipped - not
because the optimizer wouldn't fold some of those shapes too, but because this
project's precision bar requires a shape actually oracle-confirmed before
shipping it, and string/date-domain folding needs its own value-set lattice
design (a `Required`/`Excluded` set with `AND`/`OR`-aware union/intersect,
distinct from the numeric range algebra) not yet built. `WHERE`/`JOIN ... ON`
positions other than a top-level `SELECT`/`UPDATE`/`DELETE` `WHERE` clause
(`HAVING`, `JOIN ... ON`) are excluded too: an unsatisfiable `INNER JOIN ON`
predicate does make the whole join empty (same claim), but an `OUTER JOIN ON`
predicate does not - it still preserves null-extended rows from the
non-null-supplying side - and disentangling that per-join-kind case wasn't
worth mixing into this pass.

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

## Memory-optimized (Hekaton) table structural restrictions

Oracle-confirmed directly (Docker, SQL Server 2022, compat 160) against a
real `WITH (MEMORY_OPTIMIZED = ON)` table.

- **A rowstore CLUSTERED index (including the default for a bare `PRIMARY
  KEY` with no explicit `NONCLUSTERED`/`CLUSTERED` keyword) always fails
  (Msg 12317)** - a memory-optimized table has no on-disk heap/clustered
  storage at all. A clustered COLUMNSTORE index is unaffected: it is a
  legal, separate index kind on a memory-optimized table and must not be
  flagged.
- **INCLUDE columns on any index always fail (Msg 10664)**, independent of
  the index otherwise being HASH or NONCLUSTERED.
- **A filtered (`WHERE`) index is rejected by the T-SQL parser itself (Msg
  46107, "Filtered indexes are not supported on memory optimized tables"),
  not by the engine at deploy time.** A `.sql` file containing this shape
  fails to parse at all - it never reaches a scannable catalog in either
  file-parsing or a live database (the CREATE could never have deployed).
  There is no live-catalog or parsed-DDL state this can ever be caught from
  downstream; do not re-propose a catalog-level rule for it.
- **A foreign key relationship between a memory-optimized table and a
  non-memory-optimized table always fails (Msg 10778), in both directions**
  (memory-optimized table referencing a disk-based table, and a disk-based
  table referencing a memory-optimized one) - confirmed independently for
  each direction, not inferred from one.
- **`ON DELETE`/`ON UPDATE` `CASCADE`/`SET NULL`/`SET DEFAULT` on a foreign
  key fails (Msg 10794) only when both the referencing and referenced table
  are memory-optimized.** When either side is disk-based, the cross-storage
  restriction above already fires first (a foreign key spanning storage
  engines can never legally carry any referential action at all).
- **Unsupported column types**: `xml`, `sql_variant`, `text`, `ntext`,
  `image`, `timestamp`/`rowversion` (Msg 10794 for each). `VARCHAR(MAX)`/
  `NVARCHAR(MAX)`/`VARBINARY(MAX)`, a `PERSISTED` computed column, and a
  custom `IDENTITY` seed/increment other than `(1,1)` (a separate,
  independently confirmed restriction, Msg 12339 - not yet built into a
  rule) are all legal on a memory-optimized table; do not flag them.
- `hierarchyid`/`geography`/`geometry` are also documented as unsupported on
  a memory-optimized table, but this codebase's type resolver has no way to
  distinguish them from an arbitrary CLR UDT (both resolve to an unresolved/
  null `SqlType`) - not implemented for these three pending that modeling
  gap, to avoid conflating them with a genuinely unrelated CLR UDT column.

## Settled (do not re-propose)

* **`TvfCallArgumentMismatchRuleId` — shipped.** Oracle-confirmed (Docker): an
  inline table-valued function declared `(@p VARCHAR(3))` called as
  `dbo.probe_itvf('hello')` returns `'hel'` — the 5-character literal is
  silently truncated to the parameter's declared width with no error, the
  same shape as the shipped forward-direction EXEC call-site rule
  (`ProcCallArgumentMismatchRuleId`). Only inline TVFs are in scope (matched
  via `TableValuedFunctionKind.Inline`) - multi-statement and CLR TVFs are
  not. Arguments are always positional in T-SQL (no named-parameter syntax
  for functions), and there are no OUTPUT parameters, so the scanner is
  simpler than its EXEC-call-site sibling: no name/position matching table,
  no writeback direction. Only a literal or a locally-scoped variable
  argument is resolved to a static type - a column-reference argument (e.g.
  a correlated `CROSS APPLY` argument) resolves to an unknown type and is
  silently skipped rather than guessed at. Fixed a pre-existing catalog gap
  while shipping this: `CatalogBuilder` only ever registered
  `ProcedureParameterInfo` (via `AddProcedureParameters`) for
  `CREATE/ALTER PROCEDURE`, never for `CREATE/ALTER FUNCTION` (scalar or
  table-valued) even though `FunctionStatementBody` is itself a
  `ProcedureStatementBodyBase` - so `catalog.TryGetProcedureParameters` was
  silently empty for every function scope, including a function's own
  formal parameters when resolving dynamic SQL folding state inside its own
  body.

* **`STRING_AGG` result type modeled in `BuiltinFunctionTypeResolver`/
  `ScalarExpressionResolver` — not a standalone rule.** Oracle-confirmed
  (Docker) via `sys.dm_exec_describe_first_result_set`: when neither the
  value expression nor the separator is MAX-typed, `STRING_AGG`'s result is
  capped at `VARCHAR(8000)`/`NVARCHAR(4000)` regardless of aggregated row
  count — a structural fact about the type, not something that needs
  row-count analysis. A MAX-typed value expression removes the cap
  (`VARCHAR(MAX)`/`NVARCHAR(MAX)` result). A MAX-typed separator is not a
  "no cap" case at all — it's a compile-time reject (Msg 8734, "Separator
  parameter for STRING_AGG cannot be large object type"); the separator
  argument must also be a literal or variable (Msg 8733), never an arbitrary
  expression. Teaching the type resolver this cap feeds the existing
  variable-assignment `WriteLossFinding` (`LengthTruncation`) machinery
  automatically for any `SET`/`DECLARE` assigning an uncapped `STRING_AGG`
  result into a narrower target — no bespoke scanner needed.

* **`OnlineRebuildLegacyLobRuleId` — shipped for `ALTER TABLE ... REBUILD
  WITH (ONLINE = ON)`, `ALTER INDEX ALL ... REBUILD WITH (ONLINE = ON)`
  (Msg 2725), `ALTER TABLE ... ALTER COLUMN ... WITH (ONLINE = ON)`
  (Msg 11427), and `DROP INDEX ... WITH (ONLINE = ON)` (Msg 2725).**
  Oracle-confirmed (Docker): a table carrying a TEXT/NTEXT/IMAGE column
  always fails an online rebuild that touches its clustered index or heap,
  because that always includes every column. `ALTER TABLE ... REBUILD` and
  `ALTER INDEX ALL ... REBUILD` both rebuild the clustered index/heap and so
  both fail; a single named index's own `REBUILD WITH (ONLINE = ON)` only
  fails if that specific index's own key/include list carries the legacy
  large-object column - oracle-confirmed a nonclustered index that does not
  reference the column rebuilds online successfully even while the table
  carries one (this narrower single-index shape remains open). `ALTER
  COLUMN ... WITH (ONLINE = ON)` fails online if the column's type either
  currently is, or is being converted into, TEXT/NTEXT/IMAGE -
  oracle-confirmed both directions (staying TEXT/NTEXT/IMAGE, and converting
  a non-LOB column into one) fail identically. `DROP INDEX ... WITH
  (ONLINE = ON)` fails the same way only for a *clustered* index (which
  always carries every column of the table); a nonclustered index's online
  drop is unconditionally rejected for an unrelated reason (Msg 3745, "only
  a clustered index can be dropped online") regardless of any LOB column, so
  that path is deliberately not flagged by this rule. Decidable purely from
  the target table's own catalog column types (and, for `ALTER COLUMN`, the
  statement's own before/after declared types); no data inspection needed.
  Fixed a pre-existing bug while shipping the `DROP INDEX` leg: a plain
  `CREATE [CLUSTERED] INDEX` statement never set `CatalogIndex.IsClustered`
  at all, so a clustered index created that way was cataloged as
  non-clustered.

* **`UnpivotExactTypeMismatchRuleId` — shipped (Msg 8167).** Oracle-confirmed
  (Docker): every column named in an `UNPIVOT` IN-list must share exactly
  the same type - base type, length/precision/scale, and collation all
  included - not just implicit-convertibility. `INT` vs `BIGINT` conflicts,
  `VARCHAR(10)` vs `VARCHAR(20)` conflicts, and two `VARCHAR(10)` columns
  under different collations conflict, even though every one of those pairs
  converts freely elsewhere (comparison, assignment). Decidable directly
  from the source table's own catalog column types; only the simple
  `UNPIVOT` case over a plain named table is modeled - a derived table or
  subquery as the `UNPIVOT` source is not resolved and is silently skipped
  rather than guessed at.

* **`SchemaboundAliasTypeRuleId` — shipped (Msg 2792).** Oracle-confirmed
  (Docker): a `WITH SCHEMABINDING` `CREATE`/`ALTER FUNCTION` can never
  declare a parameter, a scalar `RETURNS` type, or a multi-statement
  table-valued `RETURNS @table` column using a `CREATE TYPE ... FROM` alias
  - the statement fails to compile regardless of the alias's own underlying
  type. The message's "CLR type" wording is misleading; the type tested was
  a plain `CREATE TYPE ... FROM int` alias, not CLR. Since the function
  never comes into existence, there's no live-catalog "referenced by a
  schemabound object" state to detect after the fact - the rule is a
  param/return-type check on the schemabound declaration itself.
  `CREATE VIEW`/`CREATE TRIGGER` have no parameter or return-type
  declarations of their own, so this family applies to `CREATE FUNCTION`
  only. The "invalid parsed type name" half of the original bullet was not
  separately tested.

* **`SparseColumnDisallowedTypeRuleId` — shipped (Msg 1731).**
  Oracle-confirmed (Docker): a `SPARSE` column can never be
  `TEXT`/`NTEXT`/`IMAGE`/`GEOMETRY`/`GEOGRAPHY` (per the engine's own error
  text) or `TIMESTAMP`/`ROWVERSION` (confirmed separately, not named in the
  message). `XML`, `HIERARCHYID`, and `SQL_VARIANT` are all oracle-confirmed
  to remain allowed as sparse - don't add them to the disallow-list.
  Decidable purely from the column's own declared type and `SPARSE` flag.
  General hand-authored CLR user-defined types (beyond the built-in spatial
  types) are not modeled and so are not covered by this rule.

* **`LegacyLobUtf8CollationRuleId` — shipped (Msg 4188).** Oracle-confirmed
  (Docker): a TEXT/NTEXT column's effective collation can never carry the
  `_UTF8` or `_SC` (supplementary-character-aware) flag, whether that
  collation comes from an explicit column-level `COLLATE` clause or from the
  database's own default collation. Fixed a pre-existing bug in
  `SqlTypeReferenceResolver` while shipping this: TEXT/NTEXT were missing
  from `IsStringOrBinaryFamily`, so an explicit column-level `COLLATE`
  clause on a TEXT/NTEXT column was silently dropped and never reached the
  resolved `SqlType` at all.

* **`DropProtectedObjectRuleId` — shipped for `DROP SCHEMA` non-empty (Msg
  3729) and `DROP ROLE` against a fixed database role (Msg 15150).**
  Oracle-confirmed (Docker): `DROP SCHEMA` fails unconditionally while any
  table, view, procedure, function, table-valued function, or synonym this
  scan also saw defined in that schema still exists - `IF EXISTS` on the
  `DROP SCHEMA` statement does not suppress this, since it only guards
  against the schema itself not existing, not against it being non-empty.
  Decidable from the same-scan catalog: `DatabaseCatalog.SchemaOwnsAnyKnownObject`
  checks tables/views/table-valued functions/procedures/synonyms recorded
  under that schema name. `DROP ROLE` against any of the nine fixed database
  roles (`db_owner`, `db_accessadmin`, `db_securityadmin`, `db_ddladmin`,
  `db_backupoperator`, `db_datareader`, `db_datawriter`, `db_denydatareader`,
  `db_denydatawriter`) always fails, unconditionally - oracle-confirmed via
  `sys.database_principals.is_fixed_role`; a closed, engine-fixed name list
  needing no catalog lookup at all. `DROP ROLE public` is not a reachable
  shape - `public` is a reserved keyword there and the statement itself is a
  parse error (Msg 156), not a semantic rejection. The remaining sibling leg
  (`DROP EXTERNAL DATA SOURCE`/`DROP EXTERNAL FILE FORMAT` blocked by a
  dependent external table) is still open - see `detection-tasklist.md`.

* **`AlterTableSwitchIndexedViewAlignmentRuleId` — shipped for
  Msg 11400/11401/11402/11403/11404/11405.**
  Oracle-confirmed (Docker, SQL Server 2025): if either side of an
  `ALTER TABLE ... SWITCH` is partitioned and is referenced by a
  schema-bound indexed view whose own clustered index is NOT itself
  partitioned, the SWITCH fails unconditionally (11401) - true for the
  source side and the target side alike, and true even when the other side
  of the SWITCH isn't partitioned at all. Separately, if the target table
  is referenced by more (non-disabled) indexed views than the source table,
  the SWITCH fails unconditionally (11402) - this is a raw count compare
  that runs before the engine ever checks whether any of those views'
  partitioning actually lines up, so it's decidable without column-level
  provenance. A DISABLED indexed view's clustered index does not count
  toward this check on either side (oracle-confirmed: a target-only
  disabled indexed view does not block the SWITCH) - the scanner filters
  `IsDisabled` indexes out of both the not-partitioned check and the
  reference count. Beyond that, an indexed view referencing a partitioned
  table must directly select the table's own partitioning column - not an
  expression derived from it (11403) and not a direct selection of some
  other column (11405) - resolved by parsing the view's own definition text
  and matching its select-list expression, restricted to the single-table,
  no-join case for precision. And the view's clustered index must sit on a
  partition scheme built on a partition function structurally equivalent to
  the table's own (same range direction, parameter type, and ordered
  boundary values) - oracle-confirmed this is NOT a same-scheme-name check:
  a base table and its indexed view can pick differently-named schemes over
  the same function and still switch cleanly (11400). Finally, even when
  source and target reference EQUAL counts of (non-disabled) indexed views,
  the engine additionally requires each target view to have a "matching"
  source view beyond raw count (11404) - oracle-confirmed two views that are
  each individually aligned and correctly partitioned can still fail this
  way if they don't otherwise correspond (own error text: "... but source
  table ... is only referenced by N matching indexed view(s)"). Oracle
  probing pinned "matching" down to: the same number of SELECT-list items,
  in the same order, each a structurally identical expression (bare column
  reference or literal - anything else is left undetected) with the same
  output name, plus a structurally identical WHERE clause (or none on
  either side) - column/expression order, output aliasing, and the WHERE
  predicate itself all independently break the match even when the
  partitioning column lines up fine. Resolved by parsing both views'
  definition text and comparing their query specifications structurally
  (`IndexedViewCorrespondenceMatcher`), restricted to the single-table,
  no-join, no-GROUP-BY/HAVING/TOP/DISTINCT/ORDER-BY case for precision; any
  unsupported shape resolves Unknown rather than risking a false 11404.



* **`TemporalTableHistoryIndexGapRuleId` column-mapping sibling — killed,
  the premise doesn't hold.** A system-versioned table's schema and its
  history table's schema were hypothesized to be able to drift apart
  (ordinal, type, nullability, or generated-role mismatch) while both still
  carry a live `sys.tables` pairing. Oracle-confirmed (Docker) this is not
  reachable: `ALTER TABLE ... SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE =
  ..., DATA_CONSISTENCY_CHECK = OFF))` still rejects column-count, ordinal/
  name, type, nullability, and collation mismatches between the two tables
  (Msg 13523/13524/13525/13526/13531) — `DATA_CONSISTENCY_CHECK` governs
  only period-value data consistency, not schema shape, contrary to the
  assumption that turning it off bypasses schema validation. A history
  table also cannot itself carry `GENERATED ALWAYS AS ROW START/END`
  columns without its own `PERIOD FOR SYSTEM_TIME` (Msg 13509), ruling out
  the generated-role variant. And once versioning is ON, `ALTER TABLE` on
  either side that would change the current table's or history table's
  column shape is itself rejected (Msg 13548/13550/13552) — the pairing
  cannot drift after the fact either. There is no DDL path that leaves a
  live temporal pair schema-mismatched, so this sibling rule would never
  fire on any deployable database.

* **`CartesianJoinRuleId(AlwaysFalseInnerJoinPredicate)` — shipped.**
  Oracle-confirmed (Docker, SQL Server): an `INNER JOIN` whose own `ON`
  predicate the shipped `PredicateSurvivalAnalyzer.IsUnsatisfiable` classifies
  as never-true (constant-literal contradiction, or a single-column literal
  contradiction the same algebra already proves for `WHERE`/`CHECK`) compiles
  and runs, but the join contributes zero rows every time regardless of the
  tables' real data. Confirmed the same always-false predicate on a `LEFT`/
  `RIGHT`/`FULL OUTER JOIN` does NOT collapse anything — the preserved side's
  rows still survive, null-extended — so the rule is `INNER JOIN`-only by
  design, not an oversight. Scoped to both join operands being a direct
  `NamedTableReference` (same precision-first restriction the shipped
  no-predicate cartesian-join gap detection already applies to its own
  cross-join case) — a nested/derived-table operand on either side declines
  rather than guesses a display name.

* **`OuterJoinPredicateCollapseRuleId` — shipped, scoped to the WHERE clause
  only, and to AND-conjuncts that are not themselves wrapped in an OR.**
  Oracle-confirmed (Docker, SQL Server 2025) with self-contained
  `DECLARE @t TABLE`/`VALUES` queries: a `WHERE`-clause comparison, `LIKE`,
  `IN`, or `BETWEEN` predicate against a bare column reference on an
  `OUTER JOIN`'s null-supplying side discards every row where that join
  found no match (three-valued logic: the predicate evaluates `NULL`, and
  `WHERE` drops `NULL` the same as `FALSE`), for `LEFT OUTER JOIN`,
  `RIGHT OUTER JOIN`, and `FULL OUTER JOIN` alike - confirmed to make the
  query's actual row set identical to the equivalent `INNER JOIN`. Also
  confirmed NOT to fire, and the scanner does not fire, when: the predicate
  is `OR`-ed with a guard on any column of the same alias (`OR <col> IS
  NULL`) — confirmed unmatched rows survive; the null-supplying column is
  wrapped in a function (`ISNULL(...)`/`COALESCE(...)`) rather than referenced
  bare, since the scanner only matches a direct `ColumnReferenceExpression`
  operand; or the predicate lives in a *subsequent* join's own `ON` clause
  rather than in `WHERE` — oracle-confirmed a predicate on an earlier join's
  null-supplying alias inside a later join's `ON` clause does not eliminate
  the earlier join's unmatched row (an `ON` clause failing only means no
  match for that specific join, not exclusion of the row already produced
  upstream), unlike the same predicate in `WHERE`.

  Scope narrowed deliberately at the `OR` boundary: an `OR`-wrapped
  conjunct can have an "escape hatch" disjunct unrelated to the
  null-supplying side (oracle-confirmed: `WHERE b.status = 'active' OR
  a.flag = 1` keeps the unmatched row alive when `a.flag = 1`, even with no
  `IS NULL` guard on `b`), so proving collapse under `OR` requires proving
  every disjunct fails for a null-extended row - not attempted in this pass.
  Any conjunct that is itself an `OR` (after unwrapping parens) is skipped
  entirely, including the guarded case, rather than risk a false positive by
  reasoning about disjunct coverage. `NOT`-wrapped conjuncts are skipped for
  the same reason. `HAVING` clauses and `ON` clauses of the join whose own
  null-supplying side is referenced are out of scope for this rule (an
  `ON`-clause predicate on the null-supplying side of its *own* `OUTER JOIN`
  is the ordinary, correct way to write a conditional outer join and does
  not collapse anything).

* **`TRANSLATE` in the bounded-string-builtins truncation family — killed,
  the premise doesn't apply to this function.** `REPLICATE`/`REPLACE`/
  `SPACE` are already shipped (`BoundedStringBuiltinTruncationScanner`)
  because each can produce a result *longer* than any single input argument
  (repetition/replacement growth), so a non-MAX declared return type's fixed
  8000/4000-byte cap can provably be exceeded. `TRANSLATE` cannot: it is a
  strict 1:1 character substitution, so its result's actual length always
  equals its input argument's actual length — oracle-confirmed (Docker) via
  `sp_describe_first_result_set`: for a non-MAX input, `TRANSLATE` always
  declares its return type at the full cap (`varchar(8000)`/`nvarchar(4000)`)
  *regardless of the input's own declared width* (a `varchar(10)` input
  still describes as `varchar(8000)`), and for a MAX-typed input it declares
  `varchar(max)`. Since the input's actual data can never be longer than its
  own bounded declared type, and `TRANSLATE`'s declared return width is
  always at least that wide, the runtime result can never exceed the
  declared cap — no silent truncation is reachable for any input. Confirmed
  with `DATALENGTH`: `TRANSLATE(REPLICATE(CAST('a' AS varchar(max)), 9000), 'a','b')`
  preserves the full 9000 bytes (declared `varchar(max)`, no cap at all).

* **`REGEXP_INSTR`/`REGEXP_REPLACE`/`REGEXP_LIKE`/`REGEXP_SUBSTR` MAX-typed
  argument rejection — killed, the premise is false on the shipping
  engine.** Oracle-confirmed (Docker, SQL Server 2025 RTM-CU8): none of the
  four `REGEXP_*` functions reject a `VARCHAR(MAX)`/`NVARCHAR(MAX)` argument
  at bind time or any other time. All four were exercised against a
  genuinely-MAX (16003-byte, built from `CAST(... AS VARCHAR(MAX))`
  concatenation, not a constant-folded `REPLICATE` that would itself silently
  cap at 8000) source string and all returned correct results:
  `REGEXP_INSTR` found the match at position 16001, `REGEXP_REPLACE` and
  `REGEXP_SUBSTR` operated correctly across the full 16003-byte input, and
  `REGEXP_LIKE` (a predicate, only usable in a boolean context like `WHERE`,
  not a directly `SELECT`-able scalar) matched correctly. No compile error,
  no truncation, no silent misbehavior at any tested length. Not a design
  gap — a false premise; there is nothing here to detect on the current
  engine.

* **`StringConcatNullRuleId` sibling for XML generation NULL coercion —
  killed as originally framed; the generation direction doesn't coerce at
  all.** Oracle-confirmed (Docker, SQL Server 2022): `FOR XML PATH` (element
  form, attribute form via `[@Name]`, and the `FOR XML PATH('')` string-
  concatenation idiom) never coerces a NULL source to empty string — a NULL
  column value's element/attribute is omitted from the output entirely by
  default, and `ELEMENTS XSINIL` makes that omission explicit as
  `xsi:nil="true"` rather than emitting an empty tag. The actual coercion
  lives one step later and on the read side only: `.value()` extracting a
  node that is present but marked `xsi:nil="true"` returns an empty string,
  not `NULL` (confirmed: `LEN(...)` on the extracted result is `0` and
  `IS NULL` is false) — genuinely silent, but a fact about the runtime XML
  *instance data* (does this particular document's node actually carry
  `xsi:nil`), not about the code or catalog; a `.value()` call against an
  untyped `xml` column can't be scored without inspecting stored document
  content, which is out of this tool's decidability bar. The one variant
  that stays within catalog+code (no data content needed) — a `.value()`
  call whose target element/attribute is declared `nillable="true"` in a
  `CREATE XML SCHEMA COLLECTION` that the source column is bound to — would
  need XML Schema Collection/XSD `nillable` modeling in the catalog builder
  that does not exist today (no `CREATE XML SCHEMA COLLECTION` modeling
  anywhere in the codebase), and hand-authored schema-bound typed `xml`
  columns are rare. Not re-proposed as scoped; a future schema-collection
  modeling effort could revisit the narrow nillable-element case.

* **System-versioned temporal period-column contract violations — killed,
  engine-guaranteed unreachable (and half the premise doesn't exist).**
  Oracle-confirmed (Docker, SQL Server 2022): SQL Server does not implement
  SQL:2011 `BUSINESS_TIME` (application-time) periods at all —
  `PERIOD FOR BUSINESS_TIME (...)` is a plain parser error (Msg 102,
  "Incorrect syntax near 'BUSINESS_TIME'"), not a DDL-time semantic
  rejection; there is no such T-SQL construct for a scanner to ever see, in
  a live catalog or otherwise. The `SYSTEM_TIME` half fares no better as a
  live-catalog finding: every contract violation named — a missing period
  column (Msg 13507, end/start column name not matching the
  `GENERATED ALWAYS AS ROW START/END` column), a period column not declared
  `GENERATED ALWAYS AS ROW START/END` (Msg 13504, "definition missing"), and
  a start/end precision mismatch (Msg 13513, "period columns cannot have
  different datatype precision") — is a synchronous `CREATE TABLE` hard
  failure, confirmed independently for all three. (Collation is not a
  distinct axis here: `SYSTEM_TIME` period columns are constrained to
  `DATETIME2`, which carries no collation.) No live catalog can ever contain
  a system-versioned table with a contract-violating period column, and no
  live catalog can ever contain a `BUSINESS_TIME` period at all — not a
  design gap, a false premise on both counts.

* **CLR table-valued function signature drift — killed, engine-guaranteed
  unreachable.** Oracle-confirmed (Docker, SQL Server 2022, `clr strict
  security` off): every path that could let a CLR TVF's declared SQL
  signature disagree with its assembly method's real signature is a
  synchronous hard failure, on both sides of the boundary. `CREATE FUNCTION
  ... EXTERNAL NAME` itself validates the declared T-SQL parameter types
  against the CLR method's real parameter types at creation (Msg 6552,
  "T-SQL and CLR types for parameter ... do not match") — the function is
  never created if they disagree, so the "declared signature drifts from a
  pre-existing correct binding" premise can't even get started this way.
  For the "assembly changes under an existing function" direction:
  `ALTER ASSEMBLY ... FROM <new bytes>` re-validates every dependent CLR
  routine's method signature against the new assembly and hard-fails (Msg
  6270, "the required method ... was not found with the same signature in
  the updated assembly") if a parameter type changed — confirmed with
  `WITH UNCHECKED DATA` too (that option does not relax method-signature
  checking, only UDT serialization-format checking). The same ALTER ASSEMBLY
  validation also covers a TVF's `FillRowMethodName` callback independently
  of the main method's own parameters — changing only the fill-row method's
  output parameter type is caught with the identical Msg 6270. There is no
  `DROP ASSEMBLY`-without-dropping-dependents path either (the engine
  refuses to drop an assembly while a function still references it). Since
  creation, assembly replacement, and the TVF's own row-shape callback are
  all synchronously checked and any mismatch is rejected outright, no live
  catalog can ever contain a CLR TVF whose declared signature disagrees with
  its assembly's real method signature — not a design gap, a false premise.

* **`VariableLengthKeyColumnExceedsKeyLimit` sibling for table in-row row
  size — killed, engine-guaranteed unreachable.** Oracle-confirmed (Docker,
  SQL Server 2022): unlike the index-key-length case (`CREATE INDEX` only
  warns, deferring the real failure to a later `INSERT`/`UPDATE`), the
  table-row-size boundary is enforced synchronously at DDL time and never
  produces a live catalog defect to find. A table built only from types that
  cannot be pushed off-row (fixed-length `char`/`binary`/numeric/date columns)
  hard-fails `CREATE TABLE`/`ALTER TABLE ADD COLUMN`/`ALTER COLUMN` the
  moment the minimum row size (fixed-column bytes + row header + null bitmap
  + per-variable-length-column 2-byte offset overhead, all variable columns
  assumed empty) exceeds 8060 bytes (Msg 1701, "Creating or altering table
  ... failed because the minimum row size would be N ... This exceeds the
  maximum allowable table row size of 8060 bytes") — confirmed for both
  `CREATE TABLE` and a subsequent `ALTER TABLE ADD COLUMN` against an
  already-live table (the `ALTER` is rejected outright and the table is left
  unchanged, not partially applied). For tables whose declared width comes
  from variable-length (`varchar`/`nvarchar`/`varbinary`, non-MAX) columns,
  there is no failure and no warning at all, at either `CREATE TABLE` time or
  at `INSERT` time with data that actually fills every column to its
  declared max (confirmed: a two-column `varchar(4000)`/`varchar(4060)`
  table accepted a full-width insert with no error) — SQL Server's
  row-overflow storage (in place since SQL Server 2005) silently pushes
  variable-length column data off-row as needed, so the declared-max-sum
  arithmetic that drives the shipped key-limit rule has no analogous defect
  to report here. Because every path that could create or widen a
  minimum-row-size violation is a synchronous hard failure, no table with
  this shape can ever exist in a scanned live catalog — not a design gap,
  a false premise for this tool's live-catalog scan model.

* **`sys.columns.is_ansi_padded` is not scoped to string/binary types, and
  `ALTER COLUMN` (not just `CREATE TABLE`/`ADD COLUMN`) resets it in place.**
  Oracle-confirmed (Docker, SQL Server 2022): every column created while
  `SET ANSI_PADDING` is `OFF` - including a plain `INT` column, where the flag
  has no behavioral meaning at all - carries `is_ansi_padded = 0`, so a rule
  reading this catalog column must gate on the column's own type category
  (`VARCHAR`/`NVARCHAR`/`VARBINARY`) or it false-positives on unrelated
  numeric/date columns. Separately, running `ALTER TABLE ... ALTER COLUMN`
  against an already-`OFF` column while `SET ANSI_PADDING ON` is in effect
  resets `is_ansi_padded` to `1` in place - it is not a permanent, only-at-
  creation snapshot the way the shipped `ColumnAnsiPaddingOffRuleId` fix
  guidance depends on being reversible; no `DROP`/recreate is required.

* **Partition function parameter type mismatch — killed, engine-guaranteed
  unreachable.** Oracle-confirmed (Docker, SQL Server 2022): the partitioning
  column's type/precision/scale/collation is checked against the partition
  function's own parameter at every DDL surface that could introduce a
  mismatch, and each one hard-fails instead of allowing drift. `CREATE
  TABLE ... ON scheme(col)` and `CREATE INDEX ... ON scheme(col)` both reject
  a column whose type disagrees with the function's parameter type (Msg
  7726) or whose collation disagrees (Msg 7727) - confirmed for base type,
  decimal precision/scale, and collation independently. `ALTER TABLE ALTER
  COLUMN` on the partitioning column itself (or on a column a persisted
  computed partitioning column depends on) is unconditionally blocked (Msg
  5074/4922, "object is dependent on column") regardless of what the new
  type would be - even a same-shape re-declaration. There is no `ALTER
  PARTITION SCHEME`/`ALTER PARTITION FUNCTION` surface that touches the
  parameter's type. Since both creation and mutation paths are closed, a
  live catalog can never contain this mismatch, so the finding this task
  would produce could never fire - not a design gap, a false premise. Do not
  re-propose without new evidence (e.g. a cross-version restore/attach path)
  that reintroduces drift outside these DDL surfaces.

* **`ProcCallArgumentMismatchRuleId` TVP sibling: the call-boundary type
  itself can never mismatch by shape, only by identity, and identity
  mismatches always hard-error.** Oracle-confirmed (SQL Server 2025, Docker):
  a table-valued parameter argument must be a variable declared with the
  exact same user-defined table type as the parameter - passing an
  ad-hoc `DECLARE @t TABLE(...)` variable, or a variable typed with a
  same-named-but-differently-defined type from another database, both fail
  identically with `Operand type clash` at compile time regardless of
  whether the column shapes actually agree, so there is no data-dependent
  silent shape mismatch to catch at the boundary itself. The real silent
  loss is one step earlier: an `INSERT ... VALUES` into the caller's typed
  table variable is an ordinary assignment into the type's own declared
  columns, and non-length narrowing (numeric scale rounding, Unicode-to-
  non-Unicode `?` replacement, temporal precision/offset loss) is silent
  there exactly like any other INSERT - oracle-confirmed on all three
  kinds. String/binary length overflow is the one WriteLoss kind excluded:
  unlike a scalar variable assignment, SQL Server raises a hard "String or
  binary data would be truncated" error for a table variable, so it's never
  silent (oracle-confirmed). Shipped as `ProcCallTableValuedArgumentMismatchScanner`,
  scoped to `INSERT ... VALUES` population of a table variable later passed
  as a TVP argument to a resolved EXEC call; `INSERT ... SELECT` population
  is recorded to the skip ledger rather than guessed at, since resolving
  arbitrary SELECT-list column types needs the full FROM-scope lineage
  machinery this family doesn't otherwise depend on.

* **`ColumnstoreUnsupportedColumnTypeScanner` widening: no feature-switch-
  gated type exists to widen further.** Msg 35343's type gate is now fully
  covered (`sql_variant`, `xml`, `hierarchyid`, `geometry`, `geography`,
  `ntext`, `text`, `image`, `timestamp`/`rowversion` unconditionally; MAX-
  length `varchar`/`nvarchar`/`varbinary` on nonclustered only). Oracle-
  confirmed (SQL Server 2025, Docker) there is no database-compatibility-
  level dependence for any of these — `sql_variant` on a clustered columnstore
  index fails with Msg 35343 identically at every compatibility level from
  100 through 170. An alias type over `sql_variant` isn't itself legal T-SQL
  (`CREATE TYPE ... FROM SQL_VARIANT` is rejected outright), so there's no
  alias-indirection gap to close either. Two adjacent columnstore rejections
  exist but are structurally different mechanisms, not a column-type
  restriction, and are correctly out of this scanner's scope: a sparse
  column (any type) hits Msg 35309, and a non-persisted computed column hits
  Msg 35307 — both already tracked as their own separate backlog items.

* **`IndexDesignFindingKind.NoRecomputeStatistics` already covers `CREATE
  INDEX`/`ALTER INDEX ... REBUILD WITH (STATISTICS_NORECOMPUTE = ON)`, not
  just `CREATE`/`UPDATE STATISTICS ... WITH NORECOMPUTE`.** Oracle-confirmed
  (Docker, SQL Server): `sys.stats.no_recompute` is set to `1` identically
  regardless of which DDL surface set it, and the index-backed statistics
  object it produces has `auto_created = 0`, same as any other index stat.
  `LiveCatalogReader` reads `sys.stats` directly, so `CatalogStatisticsInfo.NoRecompute`
  already carries this fact for every DDL origin without any code change -
  don't re-propose this as a distinct gap.

* **Dynamic Data Masking: shipped as `DynamicDataMaskingScanner`, two finding
  kinds.** `PredicateExposure` - a masked column used as a direct operand of
  a comparison/`BETWEEN`/`LIKE`/`IN`/`GROUP BY`/`ORDER BY` - oracle-confirmed
  the engine evaluates all of these against the real stored value regardless
  of the caller's `UNMASK` permission (`WHERE`, `JOIN ON`, `HAVING`, `CASE
  WHEN` conditions, `GROUP BY` distinct-group count, `ORDER BY` row order all
  leak the real value this way; `IS NULL` deliberately excluded - masking
  never changes a value's null-ness, oracle-confirmed, so it leaks nothing).
  `ComputedExpressionCollapse` - a masked column wrapped in any non-bare
  expression in a SELECT-list position (arithmetic, `CAST`/`CONVERT`,
  `DATEADD`, string concatenation, `ISNULL`, an aggregate) - oracle-confirmed
  the whole expression's result collapses to the masking function's fixed
  sentinel for the expression's own output type, not a value computed from
  the underlying data, on both SQL Server 2022 and 2025. Two additional
  oracle-confirmed facts feed the rule but aren't separately modeled: `ALTER
  TABLE ... ALTER COLUMN <any DataType clause>` unconditionally drops masking
  from that column (even re-declaring the identical type), so
  `CatalogBuilder.VisitAlterColumn`'s type-changing branch clears
  `IsMasked`/`MaskingFunctionName` rather than carrying them forward; and the
  engine recognizes a fifth, undocumented masking function name, `datetime()`
  (parses, arity-checked, distinct from the four public functions
  `default`/`email`/`random`/`partial`) - not given special handling since
  the scanner treats the masking function name as an opaque catalog fact, not
  a fixed enum.

* **`RemovedSecurityStoredProcedureNames` diffed against `sys.dm_os_performance_counters`
  `'Deprecated Features'` (oracle-confirmed).** `sp_change_users_login` and
  `sp_changedbowner` are tracked and now in the set. `sp_dropalias`,
  `sp_helprotect`, and `sp_helpuser` are NOT present as `instance_name` rows
  under that counter object on the running engine version - kept in the set
  anyway since they're still real deprecated names, just not corroborated by
  this particular source; don't re-run this diff expecting a different
  answer without a version change.

* **`CatalogBuilder`'s column-nullability fallback: shipped, branch-aware.**
  Oracle-confirmed (Docker, SQL Server 2025): a `CREATE TABLE` column with no
  explicit `NULL`/`NOT NULL` is created `NOT NULL` under `SET
  ANSI_NULL_DFLT_OFF ON`, `NULL` under `SET ANSI_NULL_DFLT_ON ON`, `NOT NULL`
  under `SET ANSI_NULL_DFLT_ON OFF` (this also flips it), and otherwise
  inherits the database's own `sys.databases.is_ansi_null_default_on`
  (`DatabaseCatalog.IsAnsiNullDefaultOn`). `SET ANSI_NULL_DFLT_OFF OFF` is a
  no-op on the real engine - it does not revert to `NULL`, it leaves whatever
  was already in effect untouched - and `AnsiNullDfltFlowResolver` mirrors
  that asymmetry (oracle-confirmed); `SET ANSI_NULL_DFLT_ON, ANSI_NULL_DFLT_OFF`
  combined in one statement is not legal T-SQL at all (confirmed - a syntax
  error), so no combined-flag ambiguity to model. A computed column with no
  explicit `NULL`/`NOT NULL` is excluded from this fallback entirely and
  always defaults to `NULL` regardless of `ANSI_NULL_DFLT` or the source
  expression's own nullability (oracle-confirmed against `PERSISTED` and
  non-`PERSISTED` computed columns alike); an explicit `NULL`/`NOT NULL` on a
  `PERSISTED` computed column is still honored normally. `ANSI_NULL_DFLT`
  governs plain `CREATE TABLE` only - `ALTER TABLE ADD` columns, table
  variables (`DECLARE @t TABLE`), `CREATE TYPE ... AS TABLE`, and a
  multi-statement table-valued function's `RETURNS @t TABLE(...)` all
  unconditionally default to `NULL` regardless of session or database
  `ANSI_NULL_DFLT`/`ANSI_NULL_DEFAULT` state (oracle-confirmed, including
  `ALTER TABLE ADD` on an empty table with the database-level option flipped
  both ways); `CatalogBuilder` passes `defaultNullable: true` unconditionally
  at each of those four call sites rather than resolving it. Tracking goes through
  the shared
  `AnsiNullDfltFlowResolver` (`Common/AnsiNullDfltFlowResolver.cs`), built
  on the same `ProcedureBodyFlowWalker`/`IStatementFlowPolicy` branch-merge
  walker already used for `SET ANSI_NULLS`/`SET QUOTED_IDENTIFIER` tracking -
  a `SET ANSI_NULL_DFLT_*` inside an `IF`/`WHILE` branch that cannot have run
  does not leak into code after it, and each procedure/function/trigger body
  is its own flow scope (an ambient `SET` in the deployment script does not
  leak into a module's body, matching that a module's later execution is a
  separate session). Like the existing `ANSI_NULLS`/`QUOTED_IDENTIFIER`
  trackers, this is not full data-flow analysis: after an `IF/ELSE` or
  `TRY/CATCH`, state conservatively reverts to whatever was true before the
  branch even when one arm is provably always taken (e.g. `IF 1=0 ... ELSE
  SET ANSI_NULL_DFLT_OFF ON` still reverts to the pre-branch value for code
  after the block) - a shared, pre-existing limit of the walker itself, not
  new to this rule. The dynamic-SQL path (`DynamicSqlTempTableDiscovery`)
  resolves the same per-statement map by call-site position in the enclosing
  module and synthesizes it as a `SET` prefix on the wrapped snippet, so an
  outer `SET ANSI_NULL_DFLT_OFF ON` still applies to a `CREATE TABLE` inside a
  later, separate `EXEC(...)` string - an explicit override inside the exec
  string itself still wins, since it's textually later. Both paths are
  oracle-verified end-to-end against a real database, including never-taken
  `IF`/`WHILE` branches and cross-batch module-body isolation
  (`CatalogBuilderAnsiNullDfltBranchOracleTests`,
  `DynamicSqlTempTableDiscoveryAnsiNullDfltOracleTests`). Two further gaps,
  both oracle-confirmed and both deliberately not modeled: (1) a calling
  session's own ambient `SET ANSI_NULL_DFLT_*` governs a called procedure's
  temp-table default whenever the body has no `SET` of its own - not
  statically decidable (the caller is arbitrary, possibly external
  application code), so the database-level fallback is the best available
  signal, matching this file's own scope rule; (2) `GOTO` truncates
  `ProcedureBodyFlowWalker`'s traversal of a statement list (it returns
  immediately on `GoToStatement`), so a statement textually after a `GOTO` -
  even one reachable via a forward label - gets no map entry and silently
  falls back to the database default instead of an in-scope `SET` that
  precedes the `GOTO`. This is a limit of the shared walker itself (all six
  `ProcedureBodyFlowWalker` consumers have it, not just this rule); fixing it
  needs real label/jump-target resolution across the walker, out of
  proportion for this rule alone.
* **Rule harness (`Reporting/RuleHarness/`): 5 catalog rules deliberately skip
  centralized confidence filtering.** `ColumnstoreUnsupportedColumnTypeScanner`,
  `MemoryOptimizedUnsupportedColumnTypeScanner`,
  `MemoryOptimizedUnsupportedIndexOptionScanner`,
  `MemoryOptimizedForeignKeyScanner`, and `SecurityPredicateIndexScanner`
  set `ApplyConfidenceFilter => false` on their `RuleRunner` adapter. This is
  not an oversight — the pre-harness `ScanReportBuilder` never filtered these
  five finding streams by `minimumConfidence` (confirmed against `git show
  HEAD` prior to the harness migration), so several of them ship at
  `FindingConfidence.Medium` by design and would otherwise vanish under the
  CLI's `--confidence high` default. Do not add these five back to the
  default-filtered set without checking whether their findings still surface
  at `high`.
* **Rule harness registration test does not check `RuleCatalog` linkage.**
  The original task description wanted reflection-enforced "registered ⇔
  invoked ⇔ in `RuleCatalog`" three-way equivalence. That third leg isn't
  soundly checkable: several finding `Kind` enums fan out to multiple SARIF
  rule ids via a `SarifRuleCatalog.XxxRuleId(kind)` switch, so one `IRule`
  doesn't map to one `RuleCatalog` entry. `RuleRegistrationTests` only
  enforces the two legs that are actually 1:1 — every `IRule` implementation
  in `SilentScan.Core` is present in `RuleRegistry.All`, and every
  registered `Id` is unique — which is what makes "implemented but never
  wired" fail loudly. Per-`Kind`→SARIF-id coverage is already exercised by
  `SarifReportWriterCoverageTests`/`RuleCatalogCoverageTests`.
* **`IFinding` lives in `Predicates`, not `Reporting`.** `PassOrderTests`
  enforces that `Predicates` (pass 3) never names the `Reporting` namespace
  (pass 4). Every finding record implements `IFinding` for the harness's
  centralized ordering/confidence-filtering, so the marker interface itself
  has to live at or below `Predicates`'s own pass — it's declared in
  `src/SilentScan.Core/Predicates/IFinding.cs`, and the harness (`IRule`,
  `RuleContext`, `RuleRunner`, `RuleRegistry`, one adapter per migrated
  scanner) lives in `Reporting/RuleHarness/` referencing it forward, not the
  other way round.
* **Always Encrypted: only the non-enclave index/constraint/statistics key
  case shipped (`AlwaysEncryptedKeyColumnRuleId`).** A general comparison/
  join/predicate against an enclave-required AE column, and a procedure
  parameter with mismatched declared type/length/collation/encryption
  metadata compared against an AE column, both turn out not to be
  statically decidable from T-SQL source at all: whether a connecting
  client has Always-Encrypted parameterization enabled, and what CEK/
  algorithm/type metadata it attaches to a given parameter, are TDS-
  protocol-level facts the driver supplies at execution time — nothing in
  a T-SQL script or stored procedure declaration carries them. Oracle-
  verified (against the standing Docker instance): a plain literal or
  `@variable` compared to an AE column always fails with the same generic
  "Operand type clash" (Msg 206) regardless of encryption type, enclave
  configuration, or whether the parameter's declared type matches the
  column - the source text alone can't distinguish "would work with an
  AE-enabled client" from "can never work." The index/constraint/
  statistics-key case is different and did ship: it's a pure DDL-time
  catalog fact (RANDOMIZED column + a column encryption key whose column
  master key lacks `ENCLAVE_COMPUTATIONS`), independent of any client.
* **Confidence stays.** Load-bearing in the `--confidence` filter, the SARIF
  tier, and `DynamicSqlPipeline`'s downgrade of findings that rest on an
  assumption.
* **Source-context classification** (migration script vs hot-path module) —
  dropped. No signal precise enough to avoid suppressing real findings.
* **The incumbent survey is closed.** §7.9–7.11.
* **Killed candidates stay killed.** Each has its measurement in
  Appendix 9; re-read it before re-proposing one.
* **Redundant CAST/CONVERT does not rescue sargability — do not re-propose
  suppressing it.** Oracle-confirmed: a CAST to a type identical to the
  wrapped column's own still produces a Table Scan, not a Seek. See
  "Sargability and index eligibility" above.
* **A `StatementVariantParityTests`-style reflection backstop for FROM-scope
  resolution was tried and rejected.** "Does this visitor call
  `FromScopeResolver`" isn't a reflectable signal the way Create/Alter
  method-pair existence is, and "does it override
  `ExplicitVisit(QuerySpecification)`" produces mostly noise — dozens of
  unrelated scanners visit that node for reasons having nothing to do with
  FROM-clause resolution. `ResolutionContext.CteRelations`'s non-nullable
  parameter is the real gate; a reflection test would have been a weaker,
  noisier version of what the compiler already enforces.
* **"One binder" shipped.** `FromScopeResolver`/`CteResolver`/`BaseColumnResolver`
  are now the only name-resolution path predicates go through;
  `DirectBaseTableResolver` (the second, independent bypass) is deleted.
  `SelectIntoColumnResolver` was deliberately excluded, not missed — it runs
  at catalog-build time, before Lineage exists, and CLAUDE.md's pass-ordering
  rule ("catalog building resolves against tables only, never views... because
  view resolution is Lineage's job") forbids folding a catalog-time resolver
  into a Lineage-time binder. Do not re-propose merging it in.
* **`sp_prepare`/`sp_execute` recognition — not worth a rule.** Checked
  (2026-08-20) against the local test database's ~5,000-module real sample
  set: `sys.sql_modules.definition LIKE '%sp_prepare%'`/`'%sp_execute%'`
  both return 0. This driver-generated, ODBC-prepared-statement pattern
  essentially never appears in hand-written T-SQL, so a rule for it would
  ship unexercised. Do not re-propose without new evidence it actually
  occurs in real modules.
* **ScriptDom does not dynamically honor a mid-script `SET QUOTED_IDENTIFIER`
  toggle.** Oracle-confirmed by probe: `initialQuotedIdentifiers` fixes the
  lexer's quoted-identifier mode for the whole `Parse()` call; a `SET
  QUOTED_IDENTIFIER ON/OFF` statement partway through the script does not
  change lexing for what follows it in the same parse. `SqlScriptParser.
  ParseFile` already reflects this — it parses the whole file once per
  guessed initial mode and keeps whichever guess produced fewer errors, it
  does not re-guess per `GO` batch. Currently low-impact because
  `ParseFile`'s only caller is diagnostic. If a caller ever needs correct
  per-batch behavior for a script that legitimately toggles
  `QUOTED_IDENTIFIER` mid-file, split on `GO` first with
  `GoBatchSplitter.Split` and guess the mode independently per batch rather
  than once for the whole file.
* **All three hand-rolled If/While/TryCatch/GoTo/Return/Throw walkers now go
  through `ProcedureBodyFlowWalker`.** `OutputParameterScanner` and
  `ParameterReassignmentPredicateScanner` shared the exact same shape — walk
  a statement list to its end, dispatch per-construct, clone state into
  branches, merge with a policy-supplied combine rule — and now go through
  `ProcedureBodyFlowWalker`'s `IStatementFlowPolicy<TState>` (each scanner
  supplies its own state type, per-statement effect, and branch-merge
  policy). `ScopeVariableFlowTracker` is structurally different — it isn't a
  whole-list walk to a final state, it's a walk *up to a specific target
  fragment* (`ResolveUpTo`/`ResolveIntoContainer`), so that targeted-descent
  part stays bespoke; but the sub-walk it runs once it reaches the target's
  sibling statements (the old `Advance`/`AnalyzeList`/`AnalyzeIf`/
  `AnalyzeWhileToFixpoint`/`Combine`) *is* the same whole-list-to-completion
  shape, so it now also goes through `ProcedureBodyFlowWalker` via its own
  `Policy`. That required extending the walker with an opt-in fixed-point
  mode for `While` (`WhileFixpointCap`/`StatesEqual`/
  `MarkApproximateOnCapExceeded`, all defaulted so the other two policies are
  unaffected — single pass, cap 1). One accepted behavior change: the old
  `TouchesVariable`/`AllBranches` short-circuit (skip recursing into a
  branch that provably never writes the tracked variable) was dropped rather
  than turned into a fourth walker hook — it was a pure performance
  optimization, not a correctness requirement (recursing into an
  unaffected branch is a no-op on the resulting `WriteState`), and adding a
  hook solely for it would have outweighed the benefit. Watch for it if this
  scanner shows up in a profile on a large procedure body.

* **`GeneratedAlwaysColumnExplicitInsertRuleId`/`GeneratedAlwaysColumnExplicitUpdateRuleId`
  — shipped.** Oracle-confirmed (Docker SQL Server 2022) a system-versioned
  temporal table's `GENERATED ALWAYS AS ROW START`/`ROW END` period columns:
  an `INSERT` (or MERGE `WHEN NOT MATCHED THEN INSERT`) naming a period
  column in its column list with anything but `DEFAULT` fails with Msg
  13536; an `UPDATE`/MERGE `WHEN MATCHED THEN UPDATE` `SET` clause naming a
  period column fails with Msg 13537 unconditionally — `DEFAULT` is not an
  escape on the UPDATE side, only on INSERT. Confirmed the fully-implicit
  `INSERT INTO t VALUES (...)` form (no column list) is held to the same
  rule at the period column's own physical ordinal position. Confirmed a
  `SELECT`/`EXEC` row source naming a period column in its column list
  always fails (13536) regardless of the selected value, since neither can
  supply `DEFAULT`. One surprise worth recording: SQL Server checks this
  restriction at `CREATE PROCEDURE` compile time, not only at execution —
  a procedure whose body contains a non-`DEFAULT` explicit assignment to an
  already-existing period column fails to compile at all (this is *not*
  deferred name resolution's usual "objects can not-yet-exist" leniency).
  That ruled out testing this rule via stored procedures the way most other
  Oracle test fixtures do (`CREATE PROCEDURE` in the fixture's own DDL would
  itself fail to deploy) — its Oracle tests instead parse/execute each
  scenario as ad-hoc SQL text against a live-read catalog.

* **`NonPersistedComputedColumnRuleId`/`TryCastComputedColumnPredicateRuleId`
  nondeterministic-index sibling — killed, unreachable.** Hypothesized: an
  indexed view or indexed computed column referencing a nondeterministic
  expression (e.g. `NEWID()`) as a direct DDL-time hard failure worth its
  own rule. Oracle-confirmed (Docker) the failure itself is real —
  `CREATE UNIQUE CLUSTERED INDEX` on a schema-bound view selecting
  `NEWID()` fails ("yields nondeterministic results"), and `CREATE INDEX`
  on a computed column using `NEWID()` fails ("cannot be used in an
  index... because it is non-deterministic") — but SQL Server refuses to
  ever *create* the index in the first place, same shape as the temporal
  history column-mapping gap killed above. A scanner reading only a live
  catalog would never observe an indexed nondeterministic column, so there
  is no reachable bad state for a new rule to detect.

* **`IndexDesignRuleId` duplicate-column sibling — killed, unreachable.**
  Hypothesized: an index definition repeating the same column across its
  key/include/partition/order-by lists. Oracle-confirmed (Docker)
  `CREATE INDEX ... (a) INCLUDE (a)` and `CREATE INDEX ... (a, a)` both
  reject at DDL time (Msg 1909, "duplicate column names in index") — same
  unreachable-via-live-catalog shape as the two entries above. Partition-
  column and columnstore order-by-column repetition weren't oracle-tested
  and may behave differently; re-open only if one of those specific forms
  is later shown to actually succeed at create time.

* **`AnsiPaddingMismatchRuleId` broaden-to-join/equality — killed, false
  premise.** Hypothesized: the shipped rule's `LIKE` trailing-whitespace
  boundary also affects join matching, equality, and persisted-expression
  results under different `ANSI_PADDING` states. Oracle-confirmed (Docker)
  general string equality trims trailing spaces unconditionally
  (`'ab' = 'ab   '` is always true) — this is baseline ANSI SQL comparison
  behavor, not something `ANSI_PADDING` toggles. Also confirmed
  `ANSI_PADDING OFF` does not affect `varchar` storage (`DATALENGTH`
  unchanged after insert) — that setting only governs fixed-length
  `char`/`binary` padding, a different mechanism than the shipped rule's
  `LIKE` boundary. No case found where join/equality results actually vary
  by `ANSI_PADDING` state.

* **`DBCC RULE ON/OFF` deprecated-syntax sibling — killed, doesn't exist.**
  Hypothesized: `DBCC RULE ON/OFF` toggles the same legacy `CREATE RULE`/
  `sp_bindrule` mechanism already flagged elsewhere as deprecated.
  Oracle-confirmed (Docker) `DBCC RULE` is not a real DBCC statement on
  either local instance — `DBCC HELP('RULE')` returns "No help available
  for DBCC statement 'RULE'", and the syntax itself doesn't parse. The
  premise was invented, not documented.

* **`CREATE TRIGGER` on a FILESTREAM-backed table — infra-blocked, not a
  scoping question.** FILESTREAM cannot be enabled at all on SQL Server for
  Linux (`mssql-conf set filestream.share_name`/`filestream.access_level`
  both report "not supported" on both local containers) — a platform
  limitation, not a missing package. No local setup can unblock a
  FILESTREAM-dependent oracle probe; do not re-propose until that changes.

* **`ALTER INDEX ... REBUILD PARTITION = n` partition-number ceiling is
  15000, not 14999.** Oracle-confirmed (Docker): partition number 15000 is
  valid (rejected only because it didn't exist on the probe table, Msg
  7730); partition number 15001 is rejected as out of range with the engine
  stating the valid range as "1 to 15000" (Msg 7722). Any future rule
  encoding this ceiling must use 15000, not 14999.
