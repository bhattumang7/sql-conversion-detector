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

- **`XML` parses to its own dedicated ScriptDom node
  (`XmlDataTypeReference`), never a `SqlDataTypeReference`.** Before this was
  handled explicitly, an XML column's type resolved to `null` (the same path
  `CURSOR`/`TABLE`/CLR-UDT types take) rather than `SqlTypeCategory.Xml` -
  same eventual Unknown verdict either way, but via the generic
  "operand-type-unresolved" reason instead of the more specific
  "out-of-model-category:Xml" one, and any future logic keyed on the actual
  category (rather than just null-checking) would have silently never seen
  Xml at all.
