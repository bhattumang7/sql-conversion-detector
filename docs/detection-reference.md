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

- **The full `binary`/`varbinary`/`char`/`varchar`/`nchar`/`nvarchar`
  precedence order is a single total order with no ties**, confirmed
  directly against a live instance (self-contained `UNION ALL` probes
  comparing every pair both ways): `binary < varbinary < char < varchar <
  nchar < nvarchar`, matching `SqlTypeCategory`'s own enum declaration
  order exactly. There is no divergence from the published precedence
  table for this group.

- **`json`, `hierarchyid`, `geometry`, `geography`, and `vector` have no
  implicit-conversion path to `xml`, `sql_variant`, a user-defined CLR
  type, or to each other.** Oracle-confirmed directly: every cross-type
  comparison among these categories fails to compile ("Operand type clash",
  "The data types ... are incompatible in the ... operator", or "Invalid
  operator for data type"), regardless of which side is which. Their
  relative position in a general type-precedence table is therefore
  real but behaviorally inert for any implicit-conversion decision - no
  input can ever exercise it, so there is nothing for a sargability or
  type-inference rule to get wrong here.

- **A `CASE`/`UNION`-style branch merge that lands two character-family
  branches in the same collation-coercibility tier with different actual
  collation names is the real "cannot resolve the collation conflict"
  trigger - not merely "the two branches have different collations."**
  An explicit `COLLATE` clause on one branch sits in a strictly higher
  coercibility tier than an ordinary column's collation, which in turn
  outranks a database-default collation; the higher tier always wins
  outright and silently, with no error, regardless of which branch it
  appears in. Only a genuine tie (same tier, different collation name) is
  ambiguous. A rule that treats every same-category, differently-collated
  branch pair as unresolvable will false-positive on the common case of one
  branch carrying an explicit `COLLATE` clause.

- **A legacy large-object type (`text`/`ntext`/`image`) is not a legal
  conversion target when its own collation is UTF-8 or supplementary
  -character-aware** (collation names ending `_UTF8` or `_SC`/`_SC_UTF8`
  respectively) - **the same collations are perfectly legal on a modern
  `varchar(max)`/`nvarchar(max)` target.** This is a property of the legacy
  LOB type family specifically, not of UTF-8/supplementary-character
  collations in general.

- **An explicit `COLLATE` clause always outranks the collation of whatever
  it's applied to, regardless of which kind of expression carries it** -
  not just literals. `COLLATE` can be written directly after a column
  reference, a function call, a parenthesized expression, a variable, or a
  literal, and in every case it wins over that expression's own inherited
  collation, the same coercibility ordering that governs `CASE`/`UNION`
  branch merges above.

- **`SUM`/`AVG` of a `decimal`/`numeric` argument always widen the result's
  precision to 38, regardless of the input precision** - oracle-confirmed
  directly, this is not the "input precision + 10, capped at 38" formula
  documented for some other contexts. `SUM` keeps the input's own scale;
  `AVG` widens scale to `max(input scale, 6)` on top of the precision jump
  (the same scale floor plain decimal division uses). `MIN`/`MAX` of the
  same argument do not widen precision or scale at all.

## Sargability and index eligibility

- **A column-side implicit conversion in a comparison surfaces as a
  `<PlanAffectingConvert>` element inside the plan's own `<Warnings>` (a
  direct child of `<QueryPlan>`, not of the individual `<RelOp>` the
  conversion lives under) - a second, independent showplan signal alongside
  the `<Convert Implicit="1">` AST node wrapping the column, but the two do
  not always agree.** A cross-type-family conversion (e.g. `char` compared
  against an `int`) always raises a `Cardinality Estimate`-flavoured warning
  regardless of row count, indexing, or heap-vs-clustered storage - confirmed
  at row counts from zero to several thousand. A same-family, *same-collation*
  string widening (`varchar` compared against `nvarchar`, matching collation,
  no numeric family crossing) produces no `<Warnings>` element at all even
  though the AST still carries the same `<Convert Implicit="1">` node. **A
  genuine collation *conflict* between two same-category string operands
  (two different, incompatible collation names forcing the engine to convert
  both sides) is a distinct trigger from either of the above: it raises a
  `Seek Plan`-flavoured warning immediately, confirmed even against a
  near-empty table - it is not gated by the 500-row threshold at all.** A
  separate, `Seek Plan`-flavoured warning additionally appears for the
  cross-type-family case once the table's cardinality estimate crosses a
  fixed threshold of 500 rows (confirmed to flip exactly between 499 and 500
  rows with fresh full-scan statistics) - so the 500-row gate is specific to
  the cross-family scenario; a same-category collation conflict is
  ungated. Because the warning is plan-level rather than
  operator-scoped and its `Expression` text renders the column reference
  using whatever qualification the query itself used (a bare alias if the
  query aliased the table), the element's presence is a reliable
  corroborating signal but its text cannot be reliably parsed back to a
  specific column when a plan has more than one candidate conversion.

- **The engine's comparison-operator sargability gate treats every non-bare-column operand
  identically - there is no per-wrapper-type unwrapping.** A column wrapped in a function call,
  CAST/CONVERT, or arithmetic is rejected from range-seek eligibility by the same single check
  that requires an operand to be a bare column reference; none of these wrapper shapes gets any
  special-cased handling that would let it through. This confirms there is no case where the
  engine quietly rescues a function-wrapped/CAST/arithmetic-wrapped predicate into a seek - the
  loss is unconditional, matching every one of this project's own "wrapping a column blocks the
  seek" rules with no exception to account for.

- **An explicit `CAST`/`CONVERT` of a column keeps a real `Convert` node in the plan - and loses
  the seek - even when the source and target types are 100% identical, for string and binary
  types specifically; numeric and date/time types are elided as true no-ops instead.**
  Oracle-confirmed (`SHOWPLAN_XML`): `CAST(IntCol AS INT)`/`CONVERT(DECIMAL(10,2), DecimalCol)`
  compile away entirely (no `Convert` node, still an Index Seek) when the target type matches the
  source exactly, but `CAST(VarcharCol AS VARCHAR(<same length>))` and
  `CONVERT(VARBINARY(<same length>), BinaryCol)` keep an explicit, `Implicit="0"` `Convert` node
  and force a scan regardless. `CastOrConvertOnColumn`'s no-op-conversion suppression previously
  matched on type category alone (ignoring this string/binary exception), producing a false
  negative for an identical-type string or binary cast; fixed to never suppress for a
  string-family or binary-family source type.

- **`LIKE` sargability is decided by a dedicated range-transform step, separate from the general
  comparison-operator gate above.** For a *literal* pattern, it inspects the text for a
  disqualifying wildcard; the sole exception is the single-character pattern `%` alone, which
  builds a degenerate not-NULL predicate rather than a range. Every other wildcard-containing
  literal pattern yields no seekable range. **Corrected claim**: a *non-literal* (variable or
  parameter) pattern is not rejected outright as previously documented here - oracle-confirmed
  (`SHOWPLAN_XML` against a live instance) that the engine instead attempts an Index Seek with a
  runtime-computed range for this shape, via a separate mechanism from the literal-text-inspection
  path, even when the actual pattern turns out to have a leading wildcard at execution time. The
  loss-of-sargability claim for a non-literal `LIKE` pattern was wrong and the corresponding
  finding was removed rather than kept.

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

- **A composite index cannot be range-seeked at all when the predicate set has no comparison on
  the index's own leading key column, even when a later key column is fully constrained.** Oracle-
  confirmed directly (Docker SQL Server 2022): a two-column composite index `(A, B)` compiles a
  `WHERE B = <value>` predicate to an Index Scan, while `WHERE A = <value>` on the identical index
  compiles to an Index Seek - confirming the mechanism `CompositeIndexLeadingColumnScanner`'s
  finding depends on (no predicate on the leading column forces a scan) directly from a real plan,
  not only from an unread engine-internal function.

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

- **`REGEXP_LIKE` can produce a real Index Seek even when the pattern is a variable or parameter
  - the engine derives the seek range at runtime via dedicated range-bound intrinsics, not only at
  compile time from a literal.** Oracle-confirmed (Docker, SQL Server 2025 RTM-CU8, `SHOWPLAN_XML`)
  against an indexed column: a parameterized `REGEXP_LIKE(Col, @p)` compiles to an Index Seek whose
  bounds are computed per-execution from the parameter's actual value; the plan shape does not wait
  to know what the pattern will be. What actually forces a scan is a *literal* pattern that isn't
  reducible to a leading anchor (`^`) followed by nothing but literal characters - any other
  construct anywhere in the pattern (missing anchor, wildcard, character class, a trailing anchor)
  defeats the derivation and forces an Index Scan even with a supporting index in place. `REGEXP_LIKE`
  is a boolean predicate (like `CONTAINS`/`FREETEXT`), not a general scalar function: oracle-confirmed
  that `SELECT REGEXP_LIKE(...)` and any use of it as a value expression (`x = REGEXP_LIKE(...)`)
  both fail with the engine's own syntax error; only `SELECT ... WHERE REGEXP_LIKE(...)` is accepted,
  matching the existing MAX-argument finding above. This project's ScriptDom parser dependency parses
  it into its own dedicated predicate AST node, which the rule below is built against.

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

- `TypedPredicateExtractor` (`src/SilentScan.Core/Predicates/TypedPredicateExtractor.cs`) -
  closed: every `BooleanComparisonExpression`/`BETWEEN`/`LIKE`/`IN`/`= ANY`
  leaf checks `ModuleWalker.IsDeadPredicate` before it is classified, and that
  flag is populated from the same `PredicateSurvivalAnalyzer.FindDeadComparisons`
  call `NonSargablePredicateScanner` uses, for every `WHERE`/`HAVING`/JOIN
  search condition (`ModuleWalker.WithPredicateLocation`). This is the shared
  per-predicate typed-comparison feed every sargability/conversion finding
  stream is built from, so the fix below closes it too - confirmed end-to-end
  with a dedicated regression test rather than assumed from the wiring alone.
  A `MERGE` statement's `ON`/`WHEN` search conditions are not covered by this
  push (`ExplicitVisit(MergeSpecification)`/`ExplicitVisit(MergeActionClause)`
  never call `WithPredicateLocation`) - a narrow, separately-scoped gap shared
  identically by `NonSargablePredicateScanner`'s own `MergeStatement` handling
  (it always passes `dead: false` there too), not unique to this extractor.
- `NonSargablePredicateScanner` (`src/SilentScan.Core/Predicates/NonSargablePredicateScanner.cs`) -
  closed: every predicate location now runs its flattened condition through
  the same-column-AND/`IS NULL`/range contradiction detector first and skips
  a leaf the detector marks dead before classifying it as non-sargable.
- `CatchAllPredicateScanner` - closed for its own narrow
  `(Col = @p OR @p IS NULL)` idiom via the same dead-comparison check; still
  has no general OR-tautology sweep across an unrelated hand-written
  `x = @p OR x <> @p` guard elsewhere in the same clause, but that shape isn't
  this scanner's own claim to make.
- `DuplicationScanner` - its own `DuplicationRedundantAndConditionRuleId`/
  `DuplicationMutuallyExclusiveAndConditionRuleId` findings independently
  implement the column-vs-literal same-column contradiction/redundancy check
  (shape 1) directly against numeric bounds; only its separate
  "always true/false literal comparison" check stays literal-vs-literal only,
  which is its own documented scope, not a gap.
- `PartialCompositeForeignKeyJoinScanner` - closed: a `WHERE`-side
  contradiction now short-circuits the finding before the JOIN's column-pair
  coverage is even inspected.
- `QueryAntiPatternScanner` - closed for its `HAVING`-derived
  (non-aggregate-`HAVING`-predicate) and join-column-derived
  (`MERGE ... USING` non-unique-source, `DISTINCT`-masking join-fan-out)
  checks; each now runs the same dead-comparison/unsatisfiability check
  before reporting.
- `NonUniqueUpdateSourceScanner`, `NotInNullableSubqueryScanner` - both already
  short-circuit on an unsatisfiable `WHERE`/search condition before reporting;
  closed.
- `JoinKeyUniqueness` - lower priority: reasons narrowly about a specific
  idiom (join-key uniqueness), so a stray same-column contradiction elsewhere
  in the same clause is less likely to change its verdict, but not confirmed
  immune. Still open, low priority.

### Rule-by-rule survival verdicts

The rules below carry the highest-falsifiability claims (a `never`/`forces`/
`cannot`/`only` absolute about a predicate's shape) among rules whose
rationale involves a comparison, predicate, or sargability/index-seek claim.
Each is checked against the contradiction/tautology detector above:
**Survives** (the trigger shape can't be normalized away), **At-risk** (a
plausible rescue exists and isn't guarded against yet - a real candidate
false positive), or **Needs oracle check** (plausible on paper, not
confirmed against a live instance).

**At-risk:**

- `COMPOSITE_INDEX_LEADING_COLUMN` - oracle-confirmed (Docker SQL Server): a
  same-column literal contradiction anywhere in the `WHERE` clause - even on a
  column completely unrelated to the composite index in question - compiles
  to a bare `Constant Scan` with no `Table Scan`/`Index Scan`/`Index Seek`
  operator at all (`SELECT * FROM t WHERE ColB = 5 AND ColC = 1 AND ColC = 2`
  against a table with a nonclustered `(ColA, ColB)` index, `ColC` unindexed
  and unrelated: plan is `Constant Scan`, versus `Table Scan` for
  `WHERE ColB = 5` alone). `CompositeIndexLeadingColumnScanner`'s "this index
  can never be seek-used for this predicate, forcing a scan" claim was moot
  once any such contradiction made the branch dead - the predicate never
  reaches any table or index at all, so there was no scan to have forced.
  Fixed: `CompositeIndexLeadingColumnScanner.cs` now carries a
  `PredicateSurvivalAnalyzer.IsUnsatisfiable` guard over the whole `WHERE`
  clause, the same pattern `PartialCompositeForeignKeyJoinScanner` and
  `NonUniqueUpdateSourceScanner` already use, and suppresses the finding
  whenever it fires.

**Survives (selected, representative of the rest of the reviewed set):**

- `CAST_CONVERT_ON_COLUMN`, `FUNCTION_WRAPPED_COLUMN`, `COLUMN_ARITHMETIC`,
  `DATE_YEAR_ON_COLUMN`/date-part family, `CASE_FOLD_ON_COLUMN`,
  `CHARINDEX_PREFIX_MATCH`/`LEFT_PREFIX_MATCH` (all `NonSargablePredicateScanner`
  Tier-1 findings), and `SCAN_FORCED`/`RANGE_SEEK`/`Verdict.Unknown`
  (`TypedPredicateExtractor`'s typed-comparison feed) - previously at-risk:
  the contradiction detector's column-identity match only recognized a bare
  column reference as an operand, so `CAST(x AS int) = 1 AND CAST(x AS int) = 2`
  was invisible to it on either side of an AND and both wrapped-column
  findings still fired on a branch that can never return a row. Fixed by
  widening `PredicateSurvivalAnalyzer`'s `GroupByColumn`/`TryGetColumnKey` to
  recognize an identical wrapping expression (same `CAST`/`CONVERT` target
  type, same function, or same-literal-arithmetic, over the same inner
  column) as the same grouping operand, not just an identical bare column;
  `TypedPredicateExtractor` needed no separate change since it already reuses
  this analyzer's verdict via `ModuleWalker.IsDeadPredicate`.
- Same wrapped/bare-column `NonSargablePredicateScanner`/`TypedPredicateExtractor`
  findings as above, for the OR-shaped case - previously at-risk: an `OR`
  branch whose numeric range is already fully covered by an earlier `OR`
  branch on the same column (e.g. `Col >= 3 OR Col > 5`) is provably
  redundant, but the dead-predicate detector had no subsumption check, only
  contradiction/tautology. Fixed by adding `NumericValueRangeSet.IsSubsetOf`
  and `PredicateSurvivalAnalyzer.MarkSubsumedDisjuncts`, which marks a later
  `OR` disjunct dead when its range is a subset of the union of earlier
  disjuncts' ranges on the same column; consumed the same way as the
  contradiction fix above, via `ModuleWalker.IsDeadPredicate`.
- `Verdict.OperandClash` - an oracle-probed type pair that never compiles as
  a comparison at all is a bind-time fact, resolved before the normalize/
  simplify pass this whole audit is about even runs; no runtime predicate
  fold changes whether two types are comparable.
- `Verdict.SeekPreserved` - explicitly excluded from `ScanReportBuilder`'s
  actionable findings; a normalization rescue here has no precision cost
  since nothing is asserted as broken.
- `LEADING_WILDCARD_LIKE` - a literal leading-wildcard pattern has no known
  engine fold: two wildcard `LIKE` patterns on the same column aren't provably
  contradictory from their text alone. Nothing to rescue.
- A `LIKE` predicate whose pattern is a variable or parameter rather than a
  literal used to have a dedicated finding claiming a leading wildcard "can't
  be ruled out statically, forcing a scan." Oracle-confirmed (`SHOWPLAN_XML`
  against a live instance) that this is wrong: a parameterized `LIKE`
  predicate always compiles to an attempted Index Seek with a
  runtime-computed range, even when the actual pattern turns out to have a
  leading wildcard at execution time. The engine never falls back to a scan
  for this shape, so the finding was removed rather than rescued.
- `CARTESIAN_JOIN`'s always-false-inner-join-predicate finding - this rule
  fires *because* it runs the same contradiction detector to prove the `ON`
  predicate unsatisfiable; it is the normalization-aware implementation, not
  a candidate for being rescued by one.
- `CARTESIAN_JOIN`'s join-predicate-empty-with-`WHERE`-clause finding - the
  complementary case: the `ON` predicate alone is satisfiable, but combined
  with the statement's `WHERE` clause the two sides of a direct equi-join
  (`a.Col1 = b.Col2`) are provably constrained to disjoint value sets (e.g.
  `... ON a.X = b.Y WHERE a.X = 5 AND b.Y = 10`). Oracle-confirmed the join
  then returns zero rows unconditionally. Scoped deliberately to a single
  equi-join edge between two named tables with numeric-literal constraints on
  each side; wider generality (string literals, transitive multi-hop join
  chains, composite keys) is real future work, not a rescue candidate for the
  existing shape.
- `CATCH_ALL_PREDICATE`, `CHECK_CONSTRAINT_PREDICATE_CONTRADICTION_INTERVAL`,
  `NOT_NULL_PREDICATE_CONTRADICTION`, `VIEW_CHECK_OPTION_CONTRADICTION`,
  `CHECK_CONSTRAINT_NULL_NOT_HANDLED` - already oracle-confirmed and scoped
  narrowly per the sections above; no additional rescue scenario found.
- `OUTER_JOIN_PREDICATE_COLLAPSE` - documents the engine's own null-rejection
  behavior for a `WHERE`-clause predicate over an outer join's null-supplying
  side; this is what the engine does, not a shape any fold eliminates.
- `SECURITY_PREDICATE_INDEX`, `COMPOSITE_INDEX_LEADING_COLUMN`'s catalog/index
  facts - a Row-Level Security filter predicate's own bound-column-to-index
  relationship and an index's own leading-key-column identity are static
  schema facts independent of any one query's `WHERE`-clause normalization.
- `DuplicationDuplicateSiblingConditionRuleId`,
  `DuplicationNegatedComparisonAsOppositeRuleId`,
  `DuplicationIdenticalBinaryOperandsRuleId` - these reason about procedural
  `IF`/`CASE` control flow, not a boolean expression tree the query
  optimizer's own normalize/simplify pass ever evaluates; the four
  normalization categories in scope for this audit don't apply to them at
  all.

## Built-in function determinism

`ComputedColumnDeterminismChecker` (feeds
`FullTextIndexDdlFindingKind.NonDeterministicComputedColumn`, the only
consumer) walks a computed column's expression for nondeterministic
constructs. Microsoft's own "Deterministic and Nondeterministic Functions"
reference page enumerates the built-in function determinism classification
directly - a genuine closed, documented list, not a free-text source - and is
the right starting point before reaching for the engine's own internal
per-function determinism metadata (unverified at the individual-flag level
and a strictly weaker source than Microsoft's own stated behavior). Every
addition below is still oracle-
confirmed independently (Msg 4936 on a `PERSISTED` computed column, the
cheapest reproduction of the same determinism check `FullTextIndexDdlRuleId`
depends on) rather than taken from the docs page alone, because of one
confirmed discrepancy:

- **`FORMAT`, `PARSENAME`, and `AT TIME ZONE` are oracle-confirmed
  nondeterministic** (Msg 4936 rejects each in a `PERSISTED` computed
  column) and are now covered - `FORMAT`/`PARSENAME` as `FunctionCall`
  names, `AT TIME ZONE` via its own `AtTimeZoneCall` ScriptDom node (not a
  `FunctionCall`). Each is also oracle-confirmed to reach the checker's one
  real consumer: a nonpersisted computed column built from it blocks
  `CREATE FULLTEXT INDEX` with Msg 9928, matching the scanner's own finding.
- **`TEXTPTR` is oracle-confirmed nondeterministic (Msg 4936) but not added
  to the determinism denylist** - a `TEXTPTR`-rooted computed column always
  fails full-text indexing before the engine's own determinism check is
  ever reached (Msg 7670, "not a character-based, XML, image or
  varbinary(max) type column"; the same DDL shape that reaches Msg 9928 for
  `FORMAT`/`PARSENAME`/`AT TIME ZONE` produces Msg 7670 here instead), so
  the determinism entry would be true but dead code through the checker's
  one real caller. The modeling gap this used to depend on is now closed:
  `TEXTPTR` was added to `BuiltinFunctionTypeResolver`'s fixed-return-type
  table (`VARBINARY(16)`, oracle-confirmed via `SQL_VARIANT_PROPERTY`), so
  `ComputedColumnTypeResolver` now infers a `TEXTPTR`-rooted computed
  column's type, and `FullTextIndexDdlScanner`'s `UnsupportedColumnType`
  finding fires on it directly (oracle-confirmed, Msg 7670, tested) - a
  sharper, earlier-catching finding than the determinism check would have
  been anyway.
- **`MIN_ACTIVE_ROWVERSION` is documented as always-nondeterministic but
  oracle-confirmed NOT rejected** in a `PERSISTED` computed column (Docker,
  SQL Server 2025) - the docs page is wrong for this one function. Not
  added; re-confirm oracle-side before ever trusting this specific entry
  from the docs page again.
- **`CHECKSUM(*)` and `NEXT VALUE FOR` can never appear in a computed column
  expression at all** (Msg 1789 and Msg 11719 respectively, unconditional
  parse/bind-time rejections) - neither is reachable through this rule's
  computed-column-expression shape regardless of determinism, so neither
  was added to the checker.
- **Ranking/analytic window functions** (`RANK`, `DENSE_RANK`, `ROW_NUMBER`,
  `NTILE`, `LAG`, `LEAD`, `FIRST_VALUE`, `LAST_VALUE`, `PERCENTILE_CONT`,
  `PERCENTILE_DISC`, `PERCENT_RANK`, `CUME_DIST`) require an `OVER` clause,
  which a computed column expression can never contain (windowed functions
  are restricted to the `SELECT`/`ORDER BY` list) - unreachable here for the
  same reason as `NEXT VALUE FOR`, not oracle-reprobed individually.
- **`GET_TRANSMISSION_STATUS`** (Service Broker, requires a conversation
  handle argument) was not oracle-probed - too narrow a real-world surface
  inside a computed column to be worth the setup cost; left off the
  checker's list pending an actual need.
- **`CAST`/`CONVERT` from a character type to a date/time-family type is
  conditionally nondeterministic, and is now modeled.** A plain `CAST` (no
  style parameter exists) is always nondeterministic. `CONVERT` depends
  entirely on the style code: oracle-swept the full public style-code space
  against a live instance (37 styles tested against `CONVERT(..., ..., n)`
  targeting a date/time type) and found an exact boundary - no style, or
  style `0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100, 106, 107, 109, 113`, is
  nondeterministic (Msg 4936 on `PERSISTED`); every other tested style
  (`101, 103, 104, 105, 108, 110, 111, 112, 114, 120, 121, 126, 127, 130,
  131`) is deterministic. A numeric-source conversion (e.g. `int`→
  `datetime`) is deterministic regardless of style - the rule only applies
  when the source is a character type, matching the well-known reason this
  class of conversion is locale/date-format-dependent. `CHECKSUM`'s
  bare-column-list form (deterministic, oracle-confirmed) was not modeled -
  no consumer needs it and adding an unused fact isn't worth the churn.
- **`CURRENT_TIMESTAMP`, `CURRENT_DATE`, `CURRENT_USER`, `SESSION_USER`,
  `SYSTEM_USER`, and bare `USER` never parse as a `FunctionCall` at all -
  ScriptDom gives them their own dedicated node, `ParameterlessCall`, with a
  closed `ParameterlessCallType` enum covering exactly these six.** The
  checker's prior `CURRENT_TIMESTAMP` entry in the `FunctionCall`-name
  denylist was therefore dead code - that node type is never visited for a
  parameterless keyword call, so the check could never fire. Replaced with
  an `ExplicitVisit(ParameterlessCall)` override that flags the whole node
  type unconditionally; all six enum members are oracle-confirmed
  nondeterministic (Msg 4936, `PERSISTED` computed column), so there is no
  deterministic member to carve out. `CURRENT_DATE` is a SQL Server 2025+
  addition - oracle-confirmed absent on a 2022 engine (Msg 156, "Incorrect
  syntax near the keyword 'CURRENT_DATE'") despite the same ScriptDom
  `TSql160Parser` (2022 grammar) parsing it without error - the grammar
  accepting a keyword is not evidence the connected engine implements it,
  the same lesson `StringSplitArgumentRuleId`'s engine-version gate already
  encodes for a different construct.
- **The engine's own internal builtin-determinism metadata is real and
  directly readable (a fixed-size, per-function descriptor table, one
  determinism bit per entry), but several function names appear more than
  once with conflicting flags** (`year`, `month`, `round`, `concat`,
  `current_time`, and one spatial-method name observed) - multiple internal
  registrations sharing one surface name, not distinct overloads: a
  descriptor field that looked like a possible arity discriminator turned
  out to just be the name's own character length (used by the engine's own
  name-hashing step), identical across every duplicate. The engine's own
  name-lookup structure always favors the most-recently-registered
  descriptor for a given name (newest insertion wins ties) - so for a
  duplicate name, the highest-registration-order entry is the one any real
  by-name call actually resolves to. Deduplicating on that basis reproduced
  every function this project already ships or oracle-confirmed exactly
  (`year`/`month`/`round` deterministic, `format`/`getdate`/`newid`/`rand`
  nondeterministic) and independently reproduced the `MIN_ACTIVE_ROWVERSION`
  docs-page discrepancy above (a single, non-duplicated entry, read as
  deterministic straight from the engine's own metadata) - a second,
  independent confirmation that Microsoft's docs page is wrong for that one
  function, not just a quirk of this project's own oracle probe.

Non-determinism is not only a property of a directly-called function - a
plain column reference inherits it when the column itself is a
non-deterministic computed column (or, transitively, a view column built
on one). The lineage layer's `ColumnProvenance.BaseColumn` now carries this
as a flag sourced from the same computed-column check, and it propagates
through casts, expressions, and set-operation branches the same way type
information already does. No shipped rule currently reads it - it exists so
a future determinism-sensitive rule (e.g. one that needs to know a query
result is non-deterministic because it selects from a non-deterministic
computed/indexed-view column, not because it calls `NEWID()` itself) does
not have to re-derive column ancestry from scratch.

**Full sweep of the internal builtin-determinism metadata (closing the item
above), every surviving delta oracle-confirmed via `PERSISTED` computed
column rejection, not trusted from the internal flag alone.** The raw
internal flag word is not reliable by itself - direct oracle testing found
it flags several genuinely deterministic functions (`ROUND`, `CONCAT`, the
native `MONTH`/`YEAR`/`DAY`, and the `%` modulo operator all compile fine in
a `PERSISTED` computed column despite reading "nondeterministic" in the raw
table; most likely the flag is shared with an unrelated ODBC-canonical
function-escape alias of the same name, not the native T-SQL keyword form).
Every candidate below was independently oracle-confirmed, not taken from the
raw flag - ~80 additional functions added to `ComputedColumnDeterminismChecker`,
grouped by why they're nondeterministic:
- **Catalog/schema metadata** (can change via `ALTER`/`DROP`/rename):
  `OBJECT_ID`, `OBJECT_NAME`, `OBJECTPROPERTY`, `OBJECTPROPERTYEX`, `DB_ID`,
  `DB_NAME`, `DATABASEPROPERTY`, `DATABASEPROPERTYEX`, `SCHEMA_ID`,
  `SCHEMA_NAME`, `COL_NAME`, `COL_LENGTH`, `TYPE_ID`, `TYPE_NAME`,
  `TYPEPROPERTY`, `COLUMNPROPERTY`, `INDEXPROPERTY`, `FILEPROPERTY`,
  `ASSEMBLYPROPERTY`, `COLLATIONPROPERTY`, `CONNECTIONPROPERTY`,
  `SESSIONPROPERTY`, `SERVERPROPERTY`, `INDEX_COL`, `OBJECT_DEFINITION`,
  `OBJECT_SCHEMA_NAME`.
- **Session/connection/environment state:** `APP_NAME`, `HOST_ID`,
  `HOST_NAME`, `PROGRAM_NAME`, `ORIGINAL_LOGIN`, `ORIGINAL_DB_NAME`,
  `CONTEXT_INFO`, `SESSION_CONTEXT`, `CURRENT_TRANSACTION_ID`, `XACT_STATE`,
  `CURRENT_TIMEZONE`, `CURRENT_TIMEZONE_ID`, `CURRENT_REQUEST_ID`,
  `DATABASE_PRINCIPAL_ID`, `DEFAULT_DOMAIN`, `LOGINPROPERTY`.
- **Permission/identity checks** (can change independent of the row):
  `USER_ID`, `USER_NAME`, `SUSER_ID`, `SUSER_NAME`, `SUSER_SID`,
  `SUSER_SNAME`, `IS_MEMBER`, `IS_ROLEMEMBER`, `IS_SRVROLEMEMBER`,
  `HAS_PERMS_BY_NAME`, `HAS_DBACCESS`, `PERMISSIONS`.
- **Identity/sequence/counter state:** `IDENT_CURRENT`, `IDENT_INCR`,
  `IDENT_SEED`, `SCOPE_IDENTITY`, `ROWCOUNT_BIG`, `GETANSINULL`,
  `STATS_DATE`, `CHANGE_TRACKING_CURRENT_VERSION`,
  `CHANGE_TRACKING_MIN_VALID_VERSION`.
- **Locking/lock-state:** `APPLOCK_MODE`, `APPLOCK_TEST`.
- **Cryptography** (random IV/salt per call, or key-material state that can
  change): `COMPRESS`, `DECOMPRESS`, `PWDENCRYPT`, `PWDCOMPARE`, `KEY_GUID`,
  `KEY_ID`, `KEY_NAME`, `CERTPROPERTY`, `ASYMKEY_ID`, `ASYMKEYPROPERTY`,
  `SYMKEYPROPERTY`, `SIGNBYASYMKEY`.
- **Culture/locale-dependent formatting** (the same class of hazard as the
  `CAST`/`CONVERT` style-dependence above, confirmed unconditional
  regardless of the datepart argument): `DATENAME`, `FORMATMESSAGE`.
- **SQL Server 2025 vector functions** (floating-point vector-similarity
  math; a real, previously-undocumented-anywhere finding - a fixed-input
  `VECTOR_DISTANCE`/`VECTOR_NORM`/`VECTOR_NORMALIZE` call still compiles fine
  as a plain `CAST(... AS VECTOR(n))` computed column, ruling out the
  `VECTOR` type itself as the cause): `VECTOR_DISTANCE`, `VECTOR_NORM`,
  `VECTOR_NORMALIZE`, `VECTORPROPERTY`.
- **`PARSE`/`TRY_PARSE`** - dedicated `ParseCall`/`TryParseCall` ScriptDom
  node types, not generic `FunctionCall`, so they needed their own visitor
  overrides, not a denylist entry; culture-dependent parsing, the same
  underlying hazard class as `CONVERT`'s style-dependence.
- **`TRY_CAST`/`TRY_CONVERT` char-to-date-family conversion** - a real,
  previously-missed gap: the existing `CAST`/`CONVERT` style-dependent
  date-conversion rule was never wired to the `TRY_` variants, which are
  separate ScriptDom node types (`TryCastCall`/`TryConvertCall`) and follow
  the identical style-code rule (oracle-confirmed: `TRY_CAST(varchar AS
  date)` and `TRY_CONVERT(date, varchar, 9)` both reject; `TRY_CONVERT(date,
  varchar, 112)` doesn't).
- **Explicitly checked and excluded, not silently skipped:** `BIT_LENGTH`
  and `OCTET_LENGTH` don't exist as callable functions in this engine
  version despite appearing in the internal metadata table (likely
  reserved/unimplemented ANSI-standard names); `JSON_ARRAYAGG`/
  `JSON_OBJECTAGG` are aggregates and the engine rejects any aggregate in a
  computed column expression outright (Msg 175), so their determinism is
  structurally unreachable, matching the existing ranking/window-function
  exclusion above.

`AI_GENERATE_EMBEDDINGS` is now added and oracle-confirmed - the initial
"couldn't be oracle-confirmed without a configured external model endpoint"
assumption was wrong: the determinism check happens at bind time, before
any real network call, so a throwaway `CREATE EXTERNAL MODEL` object
pointing at a nonexistent endpoint was enough (Msg 4936 fires regardless of
whether the model reference itself resolves). It's a dedicated
`AIGenerateEmbeddingsFunctionCall` ScriptDom node (its `USE MODEL` clause
isn't a normal argument list), not a generic `FunctionCall`, so it needed
its own visitor override rather than a denylist string entry - the same
class of gap `PARSE`/`TRY_PARSE` needed. The other SQL Server 2025 `AI_*`
functions (`AI_GENERATE_RESPONSE`, `AI_SUMMARIZE`, `AI_CLASSIFY`,
`AI_ANALYZE_SENTIMENT`, `AI_EXTRACT`, `AI_FIX_GRAMMAR`, `AI_TRANSLATE`) are
not recognized by this specific engine build at all (`'AI_GENERATE_RESPONSE'
is not a recognized built-in function name`, even though ScriptDom itself
already has a dedicated `AIGenerateResponseFunctionCall` node type for it) -
a parser/engine version mismatch, not a real-vs-fake question; left out
until a build that actually implements them is available to confirm
against, not added on inference alone.

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
- **`hierarchyid`/`geometry`/`geography` also always fail (Msg 10794)** on a
  memory-optimized table, identical to the other unsupported types above -
  oracle-confirmed directly. The type resolver already distinguishes these
  three built-in CLR-backed types by name from an arbitrary CLR UDT (they
  resolve to their own `SqlTypeCategory` members, not an unresolved/null
  `SqlType`), so `MemoryOptimizedUnsupportedColumnTypeScanner`'s denylist
  covers them alongside `xml`/`sql_variant`/`text`/`ntext`/`image`/
  `timestamp`.
- **A `char`/`varchar` column carrying a UTF-8 collation (`_UTF8` suffix)
  always fails (Msg 12356)** - independent of whether the table is ever
  touched by a natively compiled module; the CREATE/ALTER TABLE statement
  itself never deploys. `nvarchar`/`nchar` columns with the same collation
  deploy cleanly (the `_UTF8` flag is a no-op for already-Unicode types).
- **There is no fixed byte-size ceiling enforced on a memory-optimized
  table's row at CREATE TABLE or INSERT time** on SQL Server 2022 - directly
  probed with three `CHAR(8000)` columns (24,000+ fixed bytes) both
  deploying and accepting an INSERT with no error. The commonly cited
  8,060-byte in-row limit for memory-optimized tables does not hold on a
  current engine; do not implement a rule for it.
- **`WITH (LEDGER = ON)` combined with `MEMORY_OPTIMIZED = ON` always fails
  (Msg 12359, "Ledger tables are not supported with memory optimized
  tables.")** - a plain table-option conflict, decidable purely from the
  table's own declared options; shipped as `MemoryOptimizedLedgerConflictRuleId`.

## Natively compiled T-SQL module restrictions

Oracle-confirmed directly (Docker, SQL Server 2022, compat 160) against a
real `CREATE PROCEDURE ... WITH NATIVE_COMPILATION` / `CREATE FUNCTION ...
WITH NATIVE_COMPILATION` module.

- **A built-in function call inside a natively compiled module fails (Msg
  10794, "The function '<name>' is not supported with natively compiled
  modules.") for a specific, individually confirmed set of common functions**:
  `UPPER`, `LOWER`, `REPLACE`, `CHARINDEX`, `STUFF`, `REVERSE`, `PATINDEX`,
  `QUOTENAME`, `DATALENGTH`, `ISNUMERIC`, `ISDATE`, `HASHBYTES`, `CONCAT`,
  `FORMAT`, `SOUNDEX` - and the aggregate `STDEV` (Msg 10794 too, "The
  aggregate function 'STDEV' is not supported..."; `STDEVP`/`VAR`/`VARP` not
  individually probed but documented as the same family). `STRING_AGG` and
  `STRING_SPLIT` are denylisted from Microsoft's own unsupported-construct
  documentation, not independently oracle-probed here.
- **Microsoft's own "supported functions" list for native modules is
  incomplete on a current engine - do not treat absence from it as proof of
  rejection.** `DATENAME` compiles cleanly inside a natively compiled module
  even though only `DATEPART` (not `DATENAME`) is named in the published
  list; `COALESCE`, `IIF`, and `CAST`/`CONVERT`/`TRY_CAST`/`TRY_CONVERT` also
  compile cleanly despite not appearing in the "Built-in Functions" section
  (they are separate ScriptDom node kinds, not `FunctionCall`, and are
  rewritten to/treated as `CASE`, which SQL Server 2017+ supports). This is
  why the shipped rule is a denylist of individually confirmed-rejected
  names, never an allowlist complement.
- **`LEFT(...)`/`RIGHT(...)` also fail (Msg 10794)** but are parsed as their
  own ScriptDom node kinds (`LeftFunctionCall`/`RightFunctionCall`), not
  `FunctionCall` - shipped as unconditional (name-implied, no lookup needed)
  findings via their own `IModuleRule`/`ModuleWalker` hooks alongside the
  denylist.
- **`ERROR_MESSAGE()`/`ERROR_NUMBER()`/`ERROR_SEVERITY()`/`ERROR_STATE()`/
  `ERROR_LINE()`/`ERROR_PROCEDURE()` are supported inside a natively compiled
  module but only inside a `CATCH` block** - calling any of them elsewhere
  fails (Msg 10792, "...cannot appear outside of a catch block"; oracle-
  confirmed individually for all six), not Msg 10794. A context restriction,
  not an unsupported-function rejection - shipped as
  `NativelyCompiledErrorOutsideCatchRuleId`, tracking CATCH-block nesting via
  dedicated `ModuleWalker` enter/leave hooks around `TryCatchStatement`'s
  `CatchStatements` list (distinct from the existing `TryCatchStatement`
  enter/leave hooks, which span both the `TRY` and `CATCH` bodies).
- **A CLR user-defined type (`CREATE TYPE ... EXTERNAL NAME`) used as a
  parameter or local variable's type inside a natively compiled module always
  fails (Msg 10794, "The type '<name>' is not supported with natively
  compiled modules.")** - oracle-confirmed directly (CLR enabled on
  `mssql-silentscan-sql` for this probe: `sp_configure 'clr enabled', 1` /
  `'clr strict security', 0`; a minimal net472 CLR UDT built, deployed via
  `CREATE ASSEMBLY`/`CREATE TYPE ... EXTERNAL NAME`, then referenced by a
  `DECLARE`/parameter inside a `WITH NATIVE_COMPILATION` procedure). Decidable
  purely by name: the catalog tracks CLR UDT qualified names from
  `CreateTypeUdtStatement` and checks a native module's own parameter/DECLARE
  type references against that set - no resolution of the CLR type's actual
  shape is needed. Shipped as `NativelyCompiledClrTypeRuleId`.
- **Calling a routine that is itself not natively compiled from inside a
  natively compiled module always fails, but with a different error
  depending on the call shape**: `EXEC` against an interpreted procedure
  fails with Msg 12342 ("The EXECUTE statement in natively compiled modules
  only supports executing natively compiled modules."); calling an
  interpreted scalar function fails with Msg 12344 ("Only natively compiled
  modules can be used with natively compiled modules.") - both
  oracle-confirmed independently, and confirmed clean for the reverse
  (calling another natively compiled procedure via `EXEC` deploys cleanly).
  Shipped as `NativelyCompiledInterpretedCalleeRuleId`: the catalog tracks
  every scanned `CREATE`/`ALTER`/`CREATE OR ALTER PROCEDURE`/`FUNCTION`'s
  native-compilation status by qualified name (`DatabaseCatalog
  .AddRoutineNativeCompilation`/`TryGetRoutineIsNativelyCompiled`, populated
  in `CatalogBuilder.VisitScopedBody`), and a native module's own `EXEC`/
  function-call targets are checked against it; a callee whose own
  definition isn't among the scanned files is never treated as interpreted
  (unresolved is not evidence of rejection).
- **"Deep type" rejection beyond the denylist above was not further
  oracle-tested this pass** - the shipped denylist (see above) covers the
  specific functions individually confirmed rejected; the full unsupported
  surface is not enumerated.
- **`GENERATED ALWAYS AS ROW START/END` (temporal) on a memory-optimized
  table deploys cleanly** - oracle-tested; not the restriction the original
  task item loosely gestured at. The `LEDGER`/`MEMORY_OPTIMIZED` conflict
  above (shipped) is the closest confirmed fact found in this area.

## Settled (do not re-propose)

* **`sp_execute_external_script`'s `WITH RESULT SETS` column declaration
  (reused name / missing type / rejected type) - killed, not statically
  decidable.** The item assumed SQL Server rejects a duplicate column name,
  an untyped column, or specific "rejected" data types when the clause
  redefines this procedure's output shape. Oracle-checked (Docker, SQL
  Server 2025): a plain `EXEC <proc> WITH RESULT SETS ((x INT, x
  VARCHAR(10)))` against an ordinary procedure is accepted with no error -
  duplicate names are not rejected by the engine. An untyped column
  (`WITH RESULT SETS ((x))`) is a ScriptDom parse error already, not a
  distinguishable AST shape to flag. The documented "unsupported data
  types" list for `sp_execute_external_script`
  (`cursor`/`timestamp`/`datetime2`/`datetimeoffset`/`time`/`sql_variant`/
  `text`/`image`/`xml`/`hierarchyid`/`geometry`/`geography`/CLR UDTs)
  governs the *input* query and `@params`, not the `WITH RESULT SETS`
  output column list - Microsoft's own docs make that scope explicit.
  Whether the engine actually rejects any of those types when declared as
  *output* columns can only be observed by running a real R/Python script
  through Machine Learning Services and inspecting the result, which is
  unavailable in this environment (no ML Services runtime, EULA
  unaccepted) and, more fundamentally, not something SQL Server's own
  catalog/metadata can describe ahead of time - the procedure's actual
  output shape depends on arbitrary external-script logic, unlike a
  T-SQL procedure's `DESCRIBE FIRST RESULT SET`-able shape. Out of scope
  under the decidable-from-catalog-data rule; `ExecResultSetsShapeCandidateScanner`
  already covers `WITH RESULT SETS` shape/type mismatches for ordinary
  T-SQL procedures via live `DESCRIBE`, which is the part of this space
  that is decidable.

* **`JsonIndexRewriteEligibleRuleId` shipped — `JSON_VALUE(column, path) = value`
  never seeks a JSON index (SQL Server 2025) even when one exists on the column;
  only `JSON_CONTAINS(column, value, path) = 1` does.** Oracle-confirmed (Docker,
  SQL Server 2025) via real plan XML on a 5000-row JSON-indexed table:
  `JSON_VALUE(j,'$.a') = '2500'` compiles to a `Clustered Index Scan` regardless of
  the JSON index, while `JSON_CONTAINS(j, 2500, '$.a') = 1` against the identical
  table compiles to `Nested Loops` with `Clustered Index Seek` operators against
  the JSON index by name. `JSON_CONTAINS`'s signature is `(json_expr, sql_scalar_value,
  json_path)` — the value must be a native SQL-typed argument (an `int`/`nvarchar`
  literal, variable, or parameter), not a JSON-encoded string; passing the value as
  a quoted string (e.g. `'1'` instead of `1`) silently returns 0/false rather than
  matching. Returns `NULL` when the path doesn't exist, `1`/`0` for match/no-match
  otherwise. `CREATE JSON INDEX` requires the native `JSON` column type (rejects
  `NVARCHAR(MAX)`) and `SET QUOTED_IDENTIFIER ON`. The rule fires on the
  `JSON_VALUE(...) = value` shape (RETURNING clause or not — ScriptDom keeps the
  RETURNING type out of `FunctionCall.Parameters`, so the 2-parameter match is
  unaffected) when the column has a JSON index; it does not suppress the general
  function-wrapped-column finding, since the predicate as written stays
  non-sargable regardless. JSON indexes are tracked in `CatalogIndex` via a new
  `IsJsonIndex` flag but deliberately excluded from every existing "usable index"
  filter across `IndexCoverageScanner`, `CompositeIndexLeadingColumnScanner`,
  `SecurityPredicateIndexScanner`, `TemporalTableHistoryIndexGapScanner`, and the
  duplicate/subsumed-index, FK-leading-index, and partition-alignment checks in
  `IndexDesignScanner` — a JSON index is not seekable via a plain equality
  comparison on the column the way a B-tree index is, so treating it as one there
  would misfire.

* **`OPENJSON WITH` schema projecting a native `json`-typed column while an
  "enabling feature switch" is off - killed, no such switch exists.** The item
  assumed native `json`-type support in `OPENJSON`'s `WITH` clause is gated by
  some server/database configuration that could be off, making the projection
  silently misbehave. Oracle-checked (Docker, SQL Server 2025, RTM CU8,
  compatibility level 170 and 160 both tried): `sys.database_scoped_configurations`
  has no `PREVIEW_FEATURES` row or any other JSON-named entry on this build, and
  `sp_configure` has no JSON-related option either - the native `json` type and
  `OPENJSON ... WITH (col json '$.path' AS JSON)` both work unconditionally, at
  every compatibility level tried. There is a real, engine-verified but unrelated
  silent-failure shape in the same clause: a `WITH` column (of *any* type, `json`
  included) whose path resolves to a JSON object/array returns `NULL` instead of
  erroring unless the column definition carries the `AS JSON` modifier - but that
  is generic `OPENJSON` behavior predating the native type and not statically
  decidable, since whether a given path is object/array-valued at runtime depends
  on the JSON document's shape, not on anything in the catalog.

* **`StringSplitArgumentRuleId` family broadened beyond separator length -
  argument-type validation and 3-argument-form engine-version gate shipped;
  the `REGEXP_*` MAX-argument fold-in stays killed.** Oracle-confirmed
  (Docker, SQL Server 2022 and 2019): `STRING_SPLIT`'s first two arguments
  (string, separator) only accept character-family types - a non-character
  literal or a declared local variable/parameter of a non-character type in
  either position raises Msg 8116 at compile/bind time, a declared-type
  check independent of the argument's actual runtime value. The optional
  third argument (`enable_ordinal`) only accepts a compile-time constant
  (any variable or column reference anywhere in the expression raises Msg
  8748, confirmed to fail even at `CREATE/ALTER PROCEDURE` time when the
  reference is a procedure parameter); a constant literal whose type isn't
  int/bit raises Msg 8116, and a constant int/bit value other than 0 or 1
  raises Msg 4199. Separately, the 3-argument form itself does not exist
  before SQL Server 2022: on a SQL Server 2019 engine, any call passing a
  third argument at all - regardless of its value, including a literal 0,
  1, or NULL - raises Msg 8144 ("too many arguments"), before any of the
  other three checks would even apply. The gate is the connected engine
  instance's own major version (`SERVERPROPERTY('ProductMajorVersion')`),
  not the database's compatibility level - a SQL Server 2022 engine still
  accepts the 3-argument form with the database's compatibility level
  dropped to 150, oracle-confirmed. All four checks (argument type x2,
  enable_ordinal constant-only, enable_ordinal type, enable_ordinal value,
  engine-version gate) shipped as `StringSplitArgumentFindingKind` members
  alongside the original `SeparatorNotSingleCharacter`. The `REGEXP_*`
  MAX-argument fold-in from the original item was not attempted - that
  family was already killed as a false premise on the shipping engine (see
  the `REGEXP_INSTR`/`REGEXP_REPLACE`/`REGEXP_LIKE`/`REGEXP_SUBSTR` entry
  above), so there is nothing left to fold in. The MAX-width validation leg
  is the same false premise for `STRING_SPLIT` itself - oracle-confirmed a
  genuinely-MAX (10000+ byte, `REPLICATE(CAST(... AS VARCHAR(MAX)), n)`)
  input splits correctly with no truncation and no error, regardless of the
  input or separator argument's declared width.

* **`CreateDatabaseOptionConflictRuleId` — shipped for `CONTAINMENT = PARTIAL`
  + `CATALOG_COLLATION`; other `CREATE DATABASE` option pairs probed and
  found not to conflict.** Oracle-confirmed (Docker): `CREATE DATABASE db
  CONTAINMENT = PARTIAL WITH CATALOG_COLLATION = DATABASE_DEFAULT` always
  fails with Msg 12845 ("cannot specify both CONTAINMENT = PARTIAL and
  CATALOG_COLLATION"), decidable purely from the statement's own option
  list, the same shape as `BackupOptionConflictRuleId`/
  `RestoreOptionConflictRuleId` — closes the `CREATE DATABASE` leg of
  item 49 alongside those two. The conflict fires before the server-level
  "contained database authentication" check (confirmed with that
  `sp_configure` value at 0), so it isn't gated by server config. Also
  tested and found to **not** conflict: `FILESTREAM` + `LEDGER`,
  `CONTAINMENT = PARTIAL` + `FILESTREAM`, `CONTAINMENT = PARTIAL` +
  `LEDGER`. Not explored: `MAXSIZE`/`EDITION`/`SERVICE_OBJECTIVE` (Azure SQL
  Database syntax, likely inapplicable to on-prem `CREATE DATABASE`) and
  `PERSISTENT_LOG_BUFFER` (requires persistent-memory hardware unavailable
  on the local Docker instance) — left open only if a concrete need for
  Azure SQL Database or PMEM-hardware coverage comes up, not proactively.

* **`ViewCheckOptionContradictionRuleId` — shipped.** Oracle-confirmed
  (Docker): `CREATE VIEW dbo.V AS SELECT id, amt FROM dbo.T WHERE amt > 10
  WITH CHECK OPTION` followed by `INSERT INTO dbo.V (id, amt) VALUES (1, 5)`
  fails with Msg 550 regardless of the target table's own data — the
  rejection is a property of the literal against the view's own `WHERE`
  clause, not of runtime state. Scope: the view's `WHERE` clause must
  reference exactly one column with a literal-comparable range (comparison/
  `BETWEEN`/AND/OR, folded via the shared `CheckConstraintDomainFolder`),
  the `INSERT` must carry an explicit column list (no default-order
  guessing), and the assigned value must itself be a literal — a parameter
  or expression is silently skipped rather than guessed at. `UPDATE ...
  SET` is covered the same way; the statement's own `WHERE` clause is
  irrelevant to whether the *written* value would qualify. A view without
  `WITH CHECK OPTION` never fires even when its `WHERE` clause would be
  violated by the same literal, since the engine doesn't enforce it there.

* **`RestoreOptionConflictRuleId` — shipped for `RECOVERY`/`NORECOVERY`/
  `STANDBY` pairwise conflicts on `RESTORE`.** Oracle-confirmed (Docker):
  every pairing among the three always fails with Msg 3031 ("Option '...'
  conflicts with option(s) '...'"), decidable purely from the statement's own
  `WITH` clause, the same shape as the shipped
  `BackupOptionConflictRuleId` (`DIFFERENTIAL`/`COPY_ONLY`). `RESTORE...
  CREATE DATABASE` forbidden option combinations remain open.

* **UTF8-collation VARCHAR/CHAR target — a declared-length byte cap, not a
  character cap; a candidate `WriteLossKind` for it does not clear the
  family's own bar.** Oracle-confirmed (Docker) two separate facts about a
  target whose collation carries the `_UTF8` flag: (1) it stores every
  Unicode character exactly as written, with no `?` substitution — fixed a
  pre-existing false positive in `WriteLossClassifier.IsUnicodeReplacementRisk`,
  which flagged `UnicodeToNonUnicodeReplacement` for any non-Unicode target
  regardless of collation; (2) a value whose UTF-8 byte length exceeds the
  target's declared length is a hard error under `ANSI_WARNINGS ON` (Msg
  2628, the default) and a silent truncation only under `ANSI_WARNINGS OFF`
  — same ANSI_WARNINGS-dependent split as ordinary `LengthTruncation`, which
  is why that family is scoped to variable/parameter targets only (table
  columns hard-error by default). But a variable/parameter target can never
  actually carry a UTF8 collation in real T-SQL: `DECLARE ... COLLATE` is
  not legal syntax for locals (Msg 156), and `CREATE TYPE ... FROM
  VARCHAR(n) COLLATE ...` is rejected the same way — a type alias also
  cannot carry an explicit collation. So a `WriteLossKind` for this scoped
  the way `LengthTruncation` is (variable/parameter targets only) would be
  unreachable dead code; the only real occurrence is on table columns, where
  it is not silent by default. Not shipped as a distinct `WriteLossKind`.

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

* **`CartesianJoinRuleId(JoinPredicateEmptyWithWhereClause)` — shipped.**
  Oracle-confirmed (Docker, SQL Server): an `INNER JOIN`'s `ON` predicate can
  be satisfiable on its own yet still make the join provably empty once the
  statement's `WHERE` clause is taken into account — e.g.
  `... ON a.X = b.Y WHERE a.X = 5 AND b.Y = 10` returns zero rows every time,
  since the join forces `a.X = b.Y` while the `WHERE` clause pins them to two
  different constants. Scoped to a single direct equi-join edge
  (`ColumnReferenceExpression = ColumnReferenceExpression`) between two
  `NamedTableReference` operands, with numeric-literal comparisons on either
  column drawn from the `ON`+`WHERE` clauses combined into a range set per
  side; fires only when the two sides' ranges provably never overlap.
  Deliberately does not attempt string-literal ranges, transitive multi-hop
  join chains, or composite-key edges.

* **`LegacyLobConversionTargetRuleId` — shipped.**
  Oracle-confirmed (Docker, SQL Server, Msg 4189): a `CAST`/`CONVERT`/
  `TRY_CAST`/`TRY_CONVERT` expression that targets `TEXT`/`NTEXT` and carries
  a trailing `COLLATE` clause naming a UTF-8 or `_SC`
  (supplementary-character-aware) collation never compiles - confirmed
  unconditional regardless of the source value, and confirmed identical for
  the `TRY_` variants (the failure is a target type/collation legality check,
  not a runtime conversion outcome `TRY_CAST`/`TRY_CONVERT` could otherwise
  swallow).

* **`GroupByValidityRuleId(SelectList)`/`GroupByValidityRuleId(Having)`/
  `GroupByValidityRuleId(OrderBy)` — shipped.**
  Oracle-confirmed (Docker, SQL Server, Msg 8120/8121/8127): once a `SELECT`
  has a plain `GROUP BY` clause, every select-list/`HAVING`/`ORDER BY`
  expression must either be an aggregate function call or shape-identical to
  one of the `GROUP BY` expressions - confirmed the engine's matching is
  genuinely expression-shape based (`GROUP BY Id + 1` covers `SELECT Id + 1`
  but not `SELECT Id + 2`), and confirmed SQL Server has no
  functional-dependency-on-primary-key exception (grouping by a table's full
  primary key does not exempt its other columns, unlike some other database
  engines). Scoped to a plain `GROUP BY` (`GroupByOption.None`, no
  `ROLLUP`/`CUBE`/`GROUPING SETS`, whose validity rules differ and were not
  investigated).

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
  15000, not 14999 - shipped as `QueryAntiPatternPartitionRebuildNumberExceedsCeilingRuleId`
  (universal 1-15000 range, both `ALTER TABLE ... REBUILD PARTITION = n` and
  `ALTER INDEX ... REBUILD PARTITION = n`) and
  `QueryAntiPatternAlterTableRebuildPartitionOutOfRangeRuleId` (in-range but
  above the target table's own partition count).** Oracle-confirmed
  (Docker): partition number 15000 is valid on a table whose scheme doesn't
  have that many partitions (rejected only because it didn't exist on the
  probe table, Msg 7730 - "partition number N does not exist in index");
  partition number 15001 is rejected as out of range regardless of any
  table's scheme, with the engine stating the valid range as "1 to 15000"
  (Msg 7722). Both statement forms raise the same two messages. Any rule
  encoding this ceiling must use 15000, not 14999.

* **Partition function boundary count exceeding the 15000-partition ceiling
  - killed, engine-guaranteed unreachable.** Hypothesized: a `CREATE
  PARTITION FUNCTION` with more boundary values than the engine allows
  partitions (14999 boundaries -> 15000 partitions). Oracle-confirmed
  (Docker): the engine enforces this ceiling at `CREATE`/`ALTER PARTITION
  FUNCTION` time itself - 14999 boundary values succeeds, 15000 boundary
  values (15001 partitions) is rejected unconditionally with Msg 7719,
  "CREATE/ALTER partition function failed as only a maximum of 15000
  partitions can be created." A live catalog can therefore never contain a
  partition function whose boundary count exceeds the ceiling - same
  unreachable-via-live-catalog shape as the partition function parameter
  type mismatch entry above. Only the statement-level partition-*number*
  ceiling (a literal reference exceeding 15000 in `REBUILD PARTITION = n`,
  independent of any table's actual scheme) is reachable and is shipped,
  above.

* **`DROP` against a read-only filegroup - inconclusive, not shipped.**
  Hypothesized sibling to the shipped `ALTER TABLE SWITCH` filegroup
  checks: dropping a filegroup that is currently read-only. Oracle probe
  (Docker): `DROP FILEGROUP` on a read-only-but-still-present filegroup
  fails with Msg 5042 ("filegroup is not empty"), which fires identically
  for any filegroup still carrying a data file regardless of its read-only
  state - the probe never isolated a rejection that depends specifically on
  read-only, only on the filegroup still holding a file. Do not re-propose
  without a probe that empties the filegroup first (removes its last data
  file) and then compares DROP behavior on read-only vs. read-write with no
  file present.

* **FILESTREAM data-space compatibility mismatch - infra-blocked, not
  shipped.** Sibling to the shipped `ALTER TABLE SWITCH` filegroup checks:
  a table's `FILESTREAM_ON` clause naming a filegroup that isn't marked
  `CONTAINS FILESTREAM`. Same platform limitation as the `CREATE TRIGGER`
  FILESTREAM entry above - FILESTREAM cannot be enabled at all on SQL
  Server for Linux, so no local Docker instance can create a FILESTREAM
  filegroup to oracle-verify this against. Do not re-propose until a
  FILESTREAM-capable instance is available to probe against.

* **`VectorFunctionArgumentRuleId` shipped, broadened beyond the original
  "large-object-typed operand" leg.** Oracle-confirmed (Docker, SQL Server
  2025): `VECTOR_DISTANCE`/`VECTOR_NORM`/`VECTORPROPERTY`'s vector-position
  argument(s) reject *any* non-`VECTOR(n)` type at all (Msg 8116) - not just
  large-object types. A plain `VARCHAR(10)` column, a bare string literal
  (even one holding valid vector-literal text like `'[1,2,3]'`), and
  `SQL_VARIANT` all fail identically to `VARCHAR(MAX)`; there is no implicit
  conversion from a vector-literal string into `VECTOR(n)` at these call
  sites. Also folded in a second, distinct fact found probing the same
  functions: `VECTOR_DISTANCE`'s two vector arguments must share the same
  declared dimension - a `VECTOR(3)` paired with a `VECTOR(4)` compiles but
  fails at execution for every row (Msg 42204, "The vector dimensions ...
  do not match"), independent of the row's actual data. Shipped as one rule
  family with two kinds: `NonVectorOperand` and `DimensionMismatch`.
  Separately confirmed while building this: the ScriptDom version this repo
  pins (180.78.1, used by the default parser) parses `VECTOR(n)` as a
  dedicated `VectorDataTypeReference` node, not the generic
  `SqlDataTypeReference` with `SqlDataTypeOption.Vector` the resolver
  already switched on - that existing case was unreachable dead code for
  every real scan through the default parser, silently resolving every
  `VECTOR` column/variable/CAST target to `null` (Unknown). Fixed in
  `SqlTypeReferenceResolver` directly since it blocked this rule (and any
  future vector-typed check) from ever firing on real code.

* **`SchemaWithRejectedTypeRuleId` shipped, narrower than the original item's
  premise for `OPENXML`.** Oracle-confirmed (Docker): the item assumed one
  shared `sql_variant`/spatial/legacy-LOB reject set for both `OPENXML ...
  WITH` and `OPENROWSET(BULK ...) WITH` (inline schema). The two clauses
  actually enforce different gates. `OPENROWSET(BULK ...) WITH` rejects
  `sql_variant`/`text`/`ntext`/`image` (Msg 13801), the CLR types
  `geometry`/`geography`/`hierarchyid` (Msg 13802), and `xml` (Msg 13829) -
  all three checks fire before the source file is even opened (confirmed
  against a nonexistent file path, and identically for CSV and PARQUET
  `FORMAT`), so they are pure compile-time schema checks. `OPENXML ...
  WITH`, however, only rejects the CLR types (Msg 6632,
  "CLR types cannot be used in an OpenXML WITH clause") - `sql_variant`,
  `text`, `ntext`, and `image` columns are accepted and return real rows
  against a real prepared document handle. `xml` columns are rejected too,
  but for an unrelated, context-dependent reason (element-centric mapping
  is required whenever a WITH column is typed `xml`), not a fixed type
  gate, so it is excluded from this rule to avoid a false claim. Shipped as
  one finding type with a `Kind` per (clause, rejected-type-category) pair
  covering only the combinations oracle-confirmed as a fixed, unconditional
  reject.

* **`ExecuteAtLargeObjectParameterRuleId` shipped, broader than the original
  `EXECUTE AT DATA_SOURCE`-only item.** ScriptDom (every pinned version, up
  to 180.102.0) cannot parse the elastic-query `AT DATA_SOURCE
  data_source_name` clause at all - it is not a distinguishable AST shape,
  so the rule instead targets `EXECUTE ('...', @param, ...) AT
  linked_server_name`, the syntactically parseable sibling form - oracle
  probing confirmed both forms share the identical underlying
  remote-parameter-exchange restriction, reproducing the same crash directly
  against a real `DATA_SOURCE` target as well as against a linked server.
  Oracle-confirmed (Docker, SQL Server 2022 and 2025):
  each comma-separated value after the command-text string becomes a
  remote-call parameter, independent of the command text's own type -
  passing a `VARCHAR(MAX)`/`NVARCHAR(MAX)`/`VARBINARY(MAX)`-typed local
  variable or parameter at one of those positions does not produce a clean
  error, it kills the connection with an internal engine assertion failure
  ("pilb->m_cRef == 0", memilb.cpp) instead - reproduced identically whether
  the remote target is a real linked server or an external data source, and
  regardless of whether the remote call would otherwise have succeeded (the
  crash was observed even after the remote query had already returned rows).
  A same-position `xml`-typed value is instead rejected cleanly with Msg
  9512 ("Xml data type is not supported as a parameter to remote calls").
  Fixed-length types (e.g. `NVARCHAR(100)`) and `INT` at the same position
  are unaffected. A single MAX-typed command-text argument with no
  additional parameters does not trigger the crash - only the additional,
  comma-separated parameter positions do. Shipped as one finding family with
  two kinds: `CrashesSession` and `XmlRejected`. The crash is an
  unrecoverable native fault, not a catchable `SqlException`, so the
  integration test that reproduces it runs `sqlcmd` out-of-process (via
  `docker exec`) rather than through `Microsoft.Data.SqlClient` in-process,
  since triggering it through the ADO.NET client aborts the .NET CLR itself.

* **External file-format/data-export partition column type restrictions —
  both `CREATE EXTERNAL TABLE`'s explicit column-list form and CETAS's
  select-list-inferred form shipped; the "virtual/partition column" leg
  killed, not applicable on this engine.** Oracle-confirmed (Docker, SQL
  Server 2022, PolyBase + `hadoop connectivity` + `allow polybase export`
  enabled): a `CREATE EXTERNAL TABLE (...)` column declared `SQL_VARIANT`,
  `XML`, `HIERARCHYID`, `GEOMETRY`, `GEOGRAPHY`, `NTEXT`, `TEXT`, `IMAGE`,
  `TIMESTAMP`, or a MAX-length `VARCHAR`/`NVARCHAR`/`VARBINARY` always
  fails with Msg 46518 ("The type '...' is not supported with external
  tables"), identically across `DELIMITEDTEXT` and `PARQUET` file formats
  and independent of whether `LOCATION` resolves to anything real — a
  pure declared-schema check with no format-dependent allow-list, unlike
  the originally-scoped "per file-format allow-list" premise. `CREATE
  EXTERNAL TABLE AS SELECT` rejects the identical type set on its
  select-list-inferred column types (Msg 15877, oracle-confirmed against
  a real source table). That leg is now covered too: `ModuleWalker`/
  `IModuleRule` gained one `OnEnterCreateExternalTableStatement` hook
  (mirroring the existing `OnEnterCreateTableStatement` pair) purely so
  the rule can record a CETAS statement's top-level `QuerySpecification`
  by reference; `OnEnterQuerySpecificationScope` then recognizes that
  recorded node and resolves each select-list expression's type via the
  sanctioned `Lineage/ScalarExpressionResolver.ResolveScalarType` (the
  same entry point `TypedPredicateExtractor`'s `WriteLossFinding` path
  uses), which already covers column references, `CAST`/`CONVERT`, and
  literals. A CETAS whose top-level query is a CTE (`WITH ... SELECT`)
  is recognized without any extra work, since the outer body is still a
  plain `QuerySpecification` and the CTE relation flows through the
  walker's normal scope-chain machinery. A top-level `UNION`/`UNION
  ALL`/`EXCEPT`/`INTERSECT` (`QueryExpression` is a `BinaryQueryExpression`,
  optionally wrapped in `QueryParenthesisExpression`) is also covered now:
  the statement's `QueryExpression` tree is flattened to its leaf
  `QuerySpecification` arms and each arm is recorded and resolved
  independently, oracle-confirmed (`UNION ALL`) that the engine rejects
  on any single arm's column type regardless of the others. The "virtual
  column"/`PARTITION_COLUMNS` half of the item
  is a Synapse dedicated-SQL-pool CETAS feature, not a SQL Server one:
  ScriptDom 180.102.0's `ExternalTableOptionKind` enum has no
  partition-columns member (`Distribution`, `FileFormat`, `Location`,
  `RejectSampleValue`, `RejectType`, `RejectValue`, `SchemaName`,
  `ObjectName`, `RejectedRowLocation`, `TableOptions` only), and
  `ExternalTableColumnDefinition` carries no per-column virtual/partition
  marker — out of scope under the decidable-from-SQL-Server-catalog rule,
  not a gap in this tool. Shipped as `ExternalTableUnsupportedColumnTypeRuleId`.

* **Non-numeric JSON element (or a mismatched element count) inside a string
  literal converted to native `VECTOR(n)` — shipped, broader than the
  original "boolean element" framing.** Oracle-confirmed (SQL Server 2025):
  a well-formed JSON array containing a boolean/string/null/object element
  always fails at execution (Msg 13670: "Input JSON is not a valid Vector"),
  identically whether reached via `CAST`/`CONVERT`, a `DECLARE` initializer,
  or a `SET` assignment to a `VECTOR`-typed variable — the original item
  only asked about booleans, but string/null/object are the same class of
  bug, so all four are covered. A numeric array whose element count doesn't
  match the declared dimension fails the same way (Msg 42204). A malformed
  literal, a non-array top-level value, or a nested-array element are left
  unflagged — the engine's own error text diverges for those (e.g. a bare
  `'true'` reports "Boolean not supported" but a bare `'123'` reports
  "Malformed JSON"), so guessing a specific message for them would be
  overclaiming. Shipped as `VectorLiteralConversionRuleId` (two kinds).

* **`CONTAINS`/`FREETEXT` inside an aggregate expression — shipped, real
  restriction is unconditional, not GROUP BY-scoped.** Oracle-confirmed a
  full-text predicate nested inside a non-windowed aggregate's own
  expression (typically via `CASE WHEN CONTAINS(...) THEN 1 ELSE 0 END`)
  never compiles (Msg 30082), with no `GROUP BY` needed to trigger it — the
  original item's "aggregate/GROUP BY scope" framing conflated two things;
  the actual restriction is purely about a full-text predicate nested
  inside `SUM`/`COUNT`/`AVG`/`MIN`/`MAX`/`STRING_AGG`/etc.'s own expression
  tree. Oracle-confirmed a full-text predicate inside a *windowed* aggregate
  (one with an `OVER` clause) is unaffected and compiles fine — deliberately
  excluded. Shipped as `FullTextPredicateInAggregateRuleId`.

* **`CHANGE_TRACKING` restrictions — Always Encrypted primary key leg
  shipped; legacy-LOB leg debunked, not shipped.** Oracle-confirmed (Docker,
  SQL Server 2022): `ALTER TABLE ... ENABLE CHANGE_TRACKING` against a table
  whose primary key includes an Always Encrypted column always fails (Msg
  22118), regardless of encryption type or enclave support — a catalog-only
  structural fact (primary key columns × Always Encrypted status are both
  DDL-time facts). Shipped as `ChangeTrackingEncryptedPrimaryKeyRuleId`. The
  "change tracking already enabled on a table carrying a legacy LOB column"
  half does **not** reproduce: `ALTER TABLE ... ENABLE CHANGE_TRACKING`
  (with or without `TRACK_COLUMNS_UPDATED = ON`) against a table with a
  `TEXT`/`NTEXT`/`IMAGE` column deploys cleanly, and DML against that column
  is tracked normally — the "real engine-emitted warning" the original item
  cited (`sys.messages` 7657/7661/7673) is scoped to *full-text* change
  tracking, an unrelated SQL Server feature that happens to share the
  "change tracking" name with the table-level `CHANGE_TRACKING` feature this
  item actually meant. Do not re-propose the LOB leg under this name.

* **Joined table catalog-provably contributing nothing — won't do, design
  question not a quick win.** Confirmed by inspection: the conservative
  multiplicity/null-extension proof (no projected columns/predicates/
  grouping/ordering referencing the join, plus FK/uniqueness/nullability
  proving the join can't change row count or introduce NULL-extension) is
  substantial engineering — a general-purpose proof engine, not a
  bounded oracle-testable restriction. Left for a future pass if the project
  wants to invest in that scale of static-analysis machinery.

* **Linked-server/cross-database reference cardinality estimate — won't do,
  the sharper "fixed exactly-1-row" claim is debunked.** Oracle-confirmed
  (Docker, `SILENTSCAN_LOOPBACK` loopback linked server, SQL Server 2022,
  `SET STATISTICS PROFILE`): a remote query through a linked server gets a
  real, data-dependent cardinality estimate when the provider can reach
  remote statistics — an unfiltered remote scan estimated exactly 500 rows
  (the real row count), and a filtered scan estimated 22.36 rows (≈√500, the
  engine's standard unknown-selectivity guess formula applied to a real base
  cardinality) — never a fixed 1. The existing "close to a guess" framing in
  `QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId`'s rationale is
  accurate and is left unchanged; a sharper "always exactly 1 row" claim
  would be wrong. Do not re-propose the fixed-1-row framing.

* **Linked-server predicate losing remote pushdown over a collation
  mismatch — real, oracle-confirmed, but out of scope: not statically
  decidable.** Built a throwaway linked server between the two local Docker
  SQL instances (same Docker network, no new infrastructure needed) and read
  real `SHOWPLAN_XML` output. Confirmed: a `WHERE` predicate comparing a
  linked-server character column to a same-collation value gets embedded
  directly in the query text sent to the remote server; a
  differently-collated comparison instead produces a remote query with no
  `WHERE` clause at all, plus a local `Filter` — the entire remote table
  gets pulled and filtered locally, a real, silent performance hazard. Not
  buildable as a SilentScan rule: the tool has no `CatalogTableKind` for a
  linked-server-referenced table and no way to acquire one — it never
  connects live and never has DDL for an external server, so a remote
  column's actual collation is not decidable from the scanned source or
  from this database's own catalog, the same boundary that already governs
  every other linked-server rule in this project. Do not re-propose this as
  buildable without also proposing a live-connection or user-supplied
  remote-schema mechanism, which doesn't exist today and is a materially
  different kind of feature than a static-analysis rule.

* **CLR aggregate `Terminate`/`Accumulate` deferred-resolution after `ALTER
  ASSEMBLY` — won't do, disproportionate setup cost for a rare pattern.**
  Verifying this needs a real compiled CLR aggregate binary (an
  `IBinarySerialize` implementation, `CREATE ASSEMBLY`, `CREATE AGGREGATE`,
  then an `ALTER ASSEMBLY` swap to a binary missing the bound method) shipped
  into the test suite - a materially different, heavier oracle-testing
  investment than every other item in this pass, for a pattern the item's
  own text already flags as rare (hand-authored CLR aggregates beyond none
  of the built-ins are uncommon). Left for a future pass if CLR-aggregate
  coverage becomes a priority.

* **`CREATE`/`ALTER XML SCHEMA COLLECTION` disallowed-type restrictions —
  shipped, real restriction is `NOTATION` (anywhere) and `ID`/`IDREF` (as an
  element's own type or an extension/restriction base), not a `SPARSE`/typed
  `xml` column shape.** The previous pass's probe was misdirected, per its
  own note. Oracle-confirmed (Docker): the inline XSD text's `NOTATION`
  type is rejected everywhere it appears (Msg 9337), and `ID`/`IDREF` (or a
  type derived from either) is rejected specifically as an `xs:element`'s
  own `type=` or an `xs:extension`/`xs:restriction`'s `base=` — but **not**
  as an `xs:attribute`'s `type=`, which is the ordinary, expected use of
  `ID`/`IDREF` in XSD and registers fine (Msg 6995 only for the
  element/extension/restriction case). Namespace-aware detection (any
  prefix bound to the XML Schema namespace, not just a literal `xs:`
  prefix). Shipped as `XmlSchemaCollectionDisallowedTypeRuleId` (two kinds).

* **CLR UDT catalog-metadata validity — won't do, disproportionate setup
  cost for a rare pattern.** Same category of blocker as the CLR aggregate
  item above: verifying UDT signature-interchangeability, method
  resolution, array-conversion compatibility, and operator support all need
  real compiled CLR UDT binaries shipped into the test suite, for a pattern
  (hand-authored CLR UDTs beyond the built-in spatial types) the item's own
  text already flags as rare. Left for a future pass.

* **`sp_cursoropen`/`sp_cursorexecute` literal scroll-option bitmask/paramdef
  restrictions — won't do, low value per the item's own framing.** Usually
  client-driver-generated rather than hand-authored T-SQL, so a low
  real-world hit rate for a static analyzer aimed at reviewed, checked-in
  code. Left unshipped; re-propose only if a real hand-authored occurrence
  surfaces.

* **PolyBase/Hadoop external-table column-type restrictions — duplicate of
  the already-shipped `ExternalTableUnsupportedColumnTypeRuleId` (see
  above), not a separate item.** This item and "External file-format/
  data-export partition column type restrictions" describe the same
  engine behavior (`CREATE EXTERNAL TABLE`'s PolyBase type gate). No new
  work needed.

* **`DROP EXTERNAL DATA SOURCE`/`DROP EXTERNAL FILE FORMAT` blocked by a
  dependent external table — won't do, infra-blocked in this environment.**
  `CREATE EXTERNAL DATA SOURCE`/`CREATE EXTERNAL FILE FORMAT` both deploy
  fine against a fake `HADOOP`-type location (confirmed, used throughout the
  `ExternalTableUnsupportedColumnTypeRuleId` oracle tests), but an actual
  `CREATE EXTERNAL TABLE` (any type, not just PolyBase's rejected-type set)
  always fails and rolls back entirely in this Docker environment - the
  PolyBase Java bridge needed to open a real Hadoop connection isn't
  attached (`105019: The Remote Java Bridge has not been attached yet`), and
  a native `BLOB_STORAGE`-type data source validates its URI at `CREATE
  EXTERNAL DATA SOURCE` time too (`105080`), so there is no data-source type
  that lets a table object actually persist against a fake location. Without
  a persisted external table object, `DROP EXTERNAL DATA SOURCE`/`DROP
  EXTERNAL FILE FORMAT` can never be tested against a real dependent-object
  block in this environment. Do not re-propose until a real reachable
  external storage endpoint (or a working PolyBase Hadoop/Java bridge) is
  available to probe against.

* **Ledger table `ALTER COLUMN`/`DROP COLUMN` restrictions — real
  restrictions confirmed, won't ship this pass; needs new LEDGER
  catalog modeling.** Further oracle work (beyond the previous pass's
  inconclusive probe) found real, distinct restrictions: `DROP COLUMN`
  naming one of the table's own ledger metadata columns (the
  auto-generated `TRANSACTION_ID`/`SEQUENCE_NUMBER` `GENERATED ALWAYS`
  columns SQL Server synthesizes for `WITH (LEDGER = ON)`) always fails
  (Msg 37502); `ALTER TABLE ALTER COLUMN` against a ledger table fails once
  the column carries immutable history data the change would need to
  modify (Msg 37391, not reproduced by an empty table - explains the
  previous pass's inconclusive result). Both are real and decidable, but
  the catalog has no concept of a ledger table today - `WITH (LEDGER = ON
  (...))` isn't parsed into hidden auto-generated columns, and there is no
  `IsLedgerTable`/ledger-column-kind fact anywhere in `CatalogTable`/
  `CatalogColumn`. Modeling that (table option parsing, synthesized hidden
  columns with default-or-overridden names, an `ALTER TABLE ALTER COLUMN`
  hook cross-referencing ledger-column-kind) is a real catalog extension,
  not a quick win. Left for a future pass.

* **`ScalarUdfInlineabilityScanner` coverage gap - shipped, via a working
  oracle-verification method.** The previous pass's `OBJECTPROPERTYEX(id,
  'IsInlineable')` probe returned `NULL` for both a trivial and a
  non-inlineable function and was abandoned as unusable. The working
  signal turned out to already exist in this codebase:
  `ScalarUdfVerifier`/`PlanXmlCapture` compiles a real invocation and
  checks the actual plan XML for a `<UserDefinedFunction>` element -
  present means the engine did not inline the call, absent means it did.
  Reusing that exact technique against real SQL Server: `@@ROWCOUNT`,
  `@@ERROR`, `@@NESTLEVEL`, and `@@PROCID` each block inlining (a plan
  with `<UserDefinedFunction>` every time), the same way the
  already-covered `@@DBTS` does - none of the four were in the scanner's
  blocker list. `@@IDENTITY`, `@@TRANCOUNT`, `@@SPID`, `@@OPTIONS`,
  `ERROR_NUMBER()`, and `CHECKSUM()` were also probed and confirmed to
  inline cleanly - not blockers, correctly left uncovered.
  `RAND()`/`NEWID()`/`RAISERROR`/temp-table access inside a scalar
  function are outright compile-time rejects (Msg 443/2772) regardless of
  inlining, so a function using any of them can never exist to be
  misclassified - not a scanner gap. Shipped by widening
  `ScalarUdfInlineabilityScanner`'s existing `GlobalVariableExpression`
  check from the single `@@DBTS` name to the five-name set.

* **Item 4's encryption-state leg - shipped as a new
  `AlwaysEncryptedAssignmentMismatchRuleId` family; legacy-LOB leg
  redirected to a declaration-time reject, also shipped.** Oracle-confirmed
  (Docker, SQL Server 2022) two distinct Msg 206 ("Operand type clash")
  shapes for Always Encrypted columns, both regardless of which
  encryption type/key is on either side: (1) a bare literal assigned into
  an encrypted column via `INSERT ... VALUES` or an `UPDATE`/`MERGE` `SET`
  clause always fails - the server cannot encrypt a plaintext literal
  without a column-encryption-aware client; a `NULL` literal is exempt.
  (2) a column-to-column assignment between two columns whose encryption
  state differs - encrypted vs. plaintext in either direction, or
  deterministic vs. randomized even under the same key - always fails.
  Same-column self-assignment and matching-encryption-type assignments
  compile and run fine. A parameter/variable source is never flagged -
  the client driver is expected to encrypt it appropriately before
  sending it. Shipped as `AlwaysEncryptedAssignmentMismatchScanner`,
  covering `UPDATE`/`MERGE SET` (via the existing `AssignmentSetClause`
  hook, resolving both sides through the query's own scope chain so
  joins/aliases work) and `INSERT ... VALUES` (including `MERGE`'s
  `INSERT` action) literal targets. `INSERT ... SELECT` column-to-column
  mapping across a source table was scoped out - the target-column-list
  tracking `TypedPredicateExtractor` already does for `WriteLossFinding`
  would need to be duplicated to resolve it safely, disproportionate for
  this pass. The legacy-LOB leg, as originally framed ("an assignment
  whose source type cannot legally convert"), does not exist: oracle
  probing found `DECLARE @x TEXT`/`NTEXT`/`IMAGE` fails to compile
  unconditionally (Msg 2739, "invalid for local variables") regardless of
  whether the variable is ever assigned - the same shape as the
  collation leg's debunk (illegal syntax, not an assignment-time
  restriction). Table columns and table-variable columns of these types,
  and procedure/function parameters, remain legal. `IMAGE`'s own implicit
  conversion behavior against character/XML types was also probed and
  found genuinely asymmetric and inconsistent enough (e.g. `IMAGE`
  accepts an implicit assignment from `VARCHAR(MAX)` but not from `TEXT`,
  while the reverse direction rejects both) to risk false positives if
  modeled from `SqlType` category alone - not shipped, deprioritized
  behind the declaration-time reject, which is unconditional and safe.
  Shipped the declaration-time fact as a new `DeprecatedSyntaxFindingKind.
  LegacyLobLocalVariable` (High confidence, unlike this scanner's other
  style-level findings, since it is a guaranteed compile failure, not a
  deprecation warning).

* **`sql_variant`/`bit` comparability restrictions - killed, no distinct
  restriction exists beyond ordinary type incompatibility.** The type/predicate
  rewrite's coverage ledger carried an open item modeled on the shipped
  xml/json/legacy-large-object/spatial "operand not comparable" rule family:
  the hypothesis that `sql_variant` and `bit` might carry their own
  comparison restriction the same way those four types do. Oracle-checked
  (Docker, SQL Server): `bit` compares cleanly against `float`, `varchar`, and
  ordinary scalars with no restriction at all. `sql_variant` compares cleanly
  against nearly everything (including itself, `ORDER BY`, and mixed-type
  comparisons); the one failure found (`sql_variant` against `xml`/`image`,
  Msg 206 "Operand type clash") is the same general implicit-conversion
  incompatibility every unrelated type pair produces, not a distinct
  comparability class - unlike xml/json/legacy-lob/spatial, which fail even
  against themselves. No new rule family here; the existing "operand not
  comparable" rules already cover the real restriction class correctly as
  scoped.

* **ANSI-padding/trim per-type facet on `SqlType` - not modeled, no
  behavioral gap found.** The coverage ledger flagged a confirmed, trivial
  internal per-type ANSI-trim/padding capability check (true only when a
  specific facet bit is clear and the type is string/binary family) as a
  candidate `SqlType` facet. No `SqlType` consumer needs this distinction
  today - the shipped `AnsiPaddingOffColumnScanner`/`SetOptionScanner`
  (`ANSI_PADDING`/`ANSI_NULLS`-off findings) already cover the real,
  observable risk surface (`ANSI_PADDING OFF` silently trimming/altering
  stored values, blocking indexed features) from the session/database-option
  side, which is the side an author can actually control and get wrong; the
  per-type facet this row describes is a lower-level implementation detail
  the shipped rules don't need to re-derive. Not modeled; re-open only if a
  future rule needs to distinguish comparability/trimming behavior by type
  category specifically, not by session option.

* **Literal connection-default-collation assignment - not modeled, existing
  fallback already produces the correct outcome.** The real engine assigns
  the connection's default collation to every character literal with no
  explicit `COLLATE` clause at bind time. Checked every current consumer of
  `LiteralTypeResolver`/`ExpressionTypeInferencer` in this codebase: none
  thread an ambient default-collation value in, so a literal always resolves
  with `Collation: null` today. This does not currently cause a wrong
  verdict anywhere: `ExpressionTypeInferencer.DominantCollation`'s existing
  null-collation fallback already picks whichever side carries a real
  (column/explicit) collation over a literal's null one, which is the same
  practical outcome the engine's own coercibility ordering produces (a
  column or explicit `COLLATE` always outranks a literal's connection-default
  collation) for every rule that currently reasons about collation. Not
  modeled; would need a new ambient-collation parameter threaded through the
  entire literal-resolution call surface for no currently observable benefit.

* **Boolean (`AND`/`OR`/`NOT`) and assignment-expression type/nullability
  derivation - not modeled, no consumer.** Two related, confirmed-trivial
  engine mechanisms: boolean-expression nullability is pure three-valued-logic
  propagation (nullable if any operand nullable), and an assignment
  expression's result type is simply its left-hand side's own type, with no
  conversion node inserted at the assignment itself. Neither is modeled in
  `SqlType`/`ExpressionTypeInferencer`: nullability is tracked separately, at
  the `CatalogColumn`/`ColumnProvenance` level, not as a `SqlType` facet, and
  T-SQL `AND`/`OR`/`NOT` parse as ScriptDom's `BooleanExpression` hierarchy, a
  structurally separate tree from the `ScalarExpression` tree
  `ExpressionTypeInferencer` dispatches over - modeling either would mean
  adding a parallel boolean-expression resolver and a new nullability-carrying
  result type for facts nothing downstream currently consumes. Not modeled;
  re-open only alongside a rule that specifically needs boolean-expression
  nullability or assignment-expression typing.

* **Computed-column lineage exposing its own real underlying source
  columns - not modeled, checked for a concrete bug and found none.** The
  real engine's optimizer rewrites a reference to a dependent-index-eligible
  computed column into an explicit projection of its real source columns
  before further binding. SilentScan's own lineage
  (`ColumnProvenanceAnalysis.FindUnderlyingBaseColumns`) treats a computed
  column as a terminal base-column leaf rather than walking through to its
  defining expression's own source columns. Checked both real consumers
  (`TypedPredicateExtractor`'s expression-derived-predicate finding,
  `ModuleReachableObjectWalker`'s reachability walk) for a concrete wrong
  answer this gap would cause: neither is wrong today - a persisted computed
  column can itself carry a real index, so asking "is this column indexed"
  about the computed column's own name (what both consumers do) is the
  question that actually matters for their purpose, not "are its source
  columns indexed." No demonstrated bug; not modeled. Would need a new
  `CatalogColumn` field (the computed expression's own referenced source
  columns, populated at catalog-build time) plus a `ColumnProvenanceAnalysis`
  change to walk through it - real work, but speculative until a consumer
  that needs the distinction is identified.

* **Statement-wide scalar-UDF-inlining ceiling (100+ inlined subqueries in
  one statement disables inlining) - not modeled, no realistic corpus
  evidence.** Confirmed as a real, decidable, hard-coded threshold distinct
  from the shipped per-call inlineability blockers (`EXECUTE AS`, recursion,
  nesting depth, etc., in `ScalarUdfInlineabilityScanner`). Building it needs
  a new statement-wide mechanism: count every otherwise-inlineable scalar UDF
  invocation across an entire statement (not per function definition, which
  is what the shipped scanner analyzes) and flag when the count exceeds 100.
  No real T-SQL corpus evidence of any statement approaching this threshold
  was found or expected - a statement with 100+ distinct inlineable scalar
  UDF calls is an extreme, unrepresentative edge case. Not modeled; the
  plumbing cost (a new cross-call-site counting pass) isn't justified without
  a real corpus signal that any statement gets remotely close.
