# SilentScan detection checklist

The complete, gated candidate list of SQL Server query-level performance
problems this tool could detect. This is the working backlog — work items one
by one, check them off, and prune sections when they stop being useful.

Competitor tools are referenced generically throughout ("a commercial
schema-bound analyzer," "one small T-SQL type-checker") rather than by name —
deliberate, so nothing about a specific vendor lands in a public commit. The
real identities are recorded locally in `vendor/tool-references.md`
(gitignored, never committed) if a name is ever needed again.

Context that shapes the whole list: no living static tool does type-aware,
direction-aware, lineage-aware analysis; the tools that ever bound types flag
mismatches symmetrically and are dead or niche, while the purely syntactic
patterns are each covered by several existing linters. **Rule of admission: a
new detection should require the engine-authoritative catalog, the lineage
pass, or the plan-XML oracle to be possible at all. Precision beats recall;
every rule ships with a near-miss guard and, where verdict-bearing, an oracle
test.**

Base rates below are module counts from an aggregate pattern inventory of the
local production copy (crude LIKE-heuristic counts over ~5,000 modules —
signal, not findings; no schema details recorded here).

---

## Already shipped (context, not work)

- [x] Column-side implicit conversion in predicates — precedence direction,
      collation family (`ScanForced` vs `RangeSeek`), literal typing,
      view/iTVF lineage depth + origin, dynamic-SQL constant tracing,
      oracle confirmation via `CONVERT_IMPLICIT`-on-column.
- [x] Tier-1 syntactic non-sargability stream (`SargabilityFindingKind`):
      function-wrapped column, CAST/CONVERT on column, column arithmetic,
      leading-wildcard LIKE, non-literal LIKE pattern.
- [x] MSTVF-as-fence stream (`TvfFenceFindingKind`): direct FROM/JOIN,
      correlated `CROSS/OUTER APPLY` (ranked first — no engine version
      mitigation rescues it), fence inherited through a view/iTVF layer
      (`Lineage.TvfFenceMap`, including the inline-TVF-function-call-syntax
      case), standalone reference, `INSERT ... EXEC`. Dynamic-SQL folding and
      verify-corpus oracle confirmation both wired in
      (`TvfFenceProbeBuilder`/`TvfFenceVerifier`; marker:
      `PhysicalOp="Table-valued function"` / `StatementType="INSERT EXEC"`,
      oracle-verified directly). iTVFs never fire — oracle-verified against
      `sys.objects.type='IF'` on the local test DB.
- [x] Scalar UDF stream (`ScalarUdfFindingKind`): predicate-context invocation
      (WHERE/JOIN ON/HAVING/MERGE ON, ranked first), reached through
      view/iTVF expansion (`Lineage.ScalarUdfMap`, same depth/origin shape as
      the TVF-fence map), schema-level dependency (computed column/DEFAULT/
      CHECK constraint, catalog-only via `SchemaDependencyScanner` — no
      query-site AST needed), projection-context invocation (SELECT list/
      ORDER BY/GROUP BY/SET/variable assignment, ranked last). Every call
      carries the finding's own inlineability read (SQL 2019+ FROID: engine
      `is_inlineable` preferred, `ScalarUdfInlineabilityScanner`'s static
      blocker scan only ever explains, never asserts Inlineable on its own),
      non-schemabound constant-argument-folding defeat, and CLR data-access
      status. Dynamic-SQL folding and verify-corpus oracle confirmation both
      wired in (`ScalarUdfProbeBuilder`/`ScalarUdfVerifier`; two-probe design
      — pinned probe with `OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))`
      confirms the function reference via a `<UserDefinedFunction
      FunctionName="...">` plan element, natural probe cross-checks the
      finding's own Inlineability read via `ContainsInlineScalarTsqlUdfs="1"`
      on the `StmtSimple`). Oracle-discovered and load-bearing: the hint does
      NOT propagate into a scalar UDF called from inside a view's own
      definition (a call that inlines away under the hint at the top level
      still inlines away identically with it, even though the same view
      referenced elsewhere shows the `UserDefinedFunction` element for a
      genuinely non-inlineable function) — so every probe targets the finding's
      underlying function directly, never through the referencing view,
      mirroring `TvfFenceProbeBuilder`'s identical choice. SchemaDependency
      findings are never probed — the constraint/computed-column definition
      text they cite is already engine truth. All findings share the same
      "never guess" catalog gate: a 2-part call that doesn't resolve in the
      scalar-UDF registry (built-in or truly unknown) never fires.
- [x] Write-loss stream (`silentscan/write-loss/*`, `WriteLossKind`): silent
      DML data loss with no engine error raised — unicode-to-non-unicode
      replacement (NVARCHAR/NCHAR source into VARCHAR/CHAR: out-of-codepage
      characters become `?`), approximate-to-exact truncation (REAL/FLOAT into
      an exact integer type: fractional part dropped), numeric scale narrowing
      (DECIMAL/NUMERIC into a smaller-scale target: digits rounded away),
      temporal precision loss (DATETIME-family into DATE: time-of-day
      dropped). Every member oracle-verified by inserting a real probe row and
      reading it back. Deliberately excludes cases the engine already hard-
      errors on (too-long VARCHAR, integer overflow) — flagging something
      T-SQL already stops for you would be a false "silent" claim.
- [x] Collation conflict (`silentscan/verdict/collation-conflict`): two real
      string columns compared directly whose resolved collations are
      genuinely incompatible — Msg 468, "Cannot resolve the collation
      conflict", a compile error, not a seek/scan question.

---

## Corrections to shipped work (clear before starting a new stream)

One confirmed false positive, one decision to re-examine, two omissions. No
new detections here. The false positive outranks every Tier 1 item: a shipped
rule that fires on a predicate the engine seeks is exactly the failure the
precision bar exists to prevent, and it is in the stream the Tier 1
"Type-aware upgrade of the sargability stream" section builds on.

- [x] **`JSON_VALUE(col, '$.path')` false-positives the shipped
      function-wrapped-column rule** — fixed: `JsonComputedColumnMatcher`
      (`src/SilentScan.Core/Predicates/JsonComputedColumnMatcher.cs`, wired into
      `Predicates/NonSargablePredicateScanner.cs`) reparses
      each computed column's definition text (the same throwaway-wrapper-
      statement trick `SchemaDependencyScanner` already uses) and suppresses
      `FunctionWrappedColumn` only on an exact AST match — same function, same
      source column, same literal path string, ordinal comparison — against an
      indexed computed column. Oracle-verified against the Docker instance
      (SQL Server 2022, compat 160): a matching indexed computed column
      produces a genuine `Index Seek`; a different path on an indexed computed
      column still scans (`FUNCTION_WRAPPED_COLUMN_json_value_different_path_fires.sql`
      guards exactly this). Fixtures:
      `FUNCTION_WRAPPED_COLUMN_json_value_{fires,clean}.sql` +the different-path
      guard; oracle coverage in `JsonComputedColumnSuppressionTests` (file-mode
      catalog + the live engine-authoritative pipeline + a real
      `PhysicalOp="Index Seek"` plan-XML probe with a literal comparison value,
      not a MAX-typed variable — a MAX-typed comparison value was oracle-found
      to defeat the seek even with the matching index present, tracked
      separately under Tier 1 "Oversized and MAX-typed parameters"). Coverage-empty
      against the local RM_ test database today (0 computed columns reference
      JSON_VALUE/JSON_QUERY there) — a real, fixable false positive with no
      local corpus signal, not evidence it's rare in the wild.
- [x] **Re-examine whole-plan `GetRangeThroughConvert` attribution in the
      plan-cache path.** `src/SilentScan.Live/Catalog/LivePlanCacheReader.cs:212`
      sets `hasRangeSeek` from `planXml.Contains("GetRangeThroughConvert")`
      and applies it to every conversion the detector finds in that plan.
      **This is deliberate, not an oversight** — the code comment states the
      marker is read plan-wide to match how `TypeMatrixGenerator`'s oracle
      probe reads the same signal, and `WorkloadVerdict`'s own doc comment
      discloses it. The reason to revisit: in the matrix generator each probe
      is a single authored predicate, so plan-wide and per-node are the same
      thing there, whereas a cached plan from real application SQL can carry
      several conversions at once — and the marker actually sits inside one
      specific seek predicate's `ScalarString`. Where those differ, a plan
      with one range-seek conversion marks its genuinely `ScanForced`
      siblings `RangeSeek` too. Decide with evidence rather than by argument:
      find (or author, in the standing Docker instance) a plan carrying a
      range-seek conversion and a scan-forcing conversion together, and see
      whether the misattribution is real. If it is, resolve the marker
      against the conversion node `ConvertImplicitDetector` already located.
      If multi-conversion plans can't actually arise here, record that and
      close the item — the current behavior is then correct as written.
      **Resolved: the misattribution was real.** Oracle-authored a single
      cached plan (a UNION ALL of a Windows-collation range-seek predicate and
      a SQL_\*-collation scan-forced predicate on the same table) and confirmed
      `GetRangeThroughConvert` appears once, scoped to the range-seeking
      branch's own `RelOp`, while the scan-forced branch's `RelOp` has no
      `SeekPredicates` entry for its column at all — the old plan-wide
      `.Contains()` read would have marked both `RangeSeek`. Fixed:
      `ConvertImplicitFinding` now carries a per-node `RangeSeekBound` field
      (`src/SilentScan.Verify/Oracle/ConvertImplicitFinding.cs`),
      `ConvertImplicitDetector.FindColumnConversions` computes it by walking
      each `Convert` node up to its nearest ancestor `RelOp` and checking
      *that* operator's own `SeekPredicates`/`SeekPredicateNew`
      `RangeColumns` for the same column
      (`src/SilentScan.Verify/Oracle/ConvertImplicitDetector.cs`), and
      `LivePlanCacheReader.AccumulateConversions` now ORs in
      `conversion.RangeSeekBound` per column instead of a plan-wide boolean
      (`src/SilentScan.Live/Catalog/LivePlanCacheReader.cs:212`).
      `TypeMatrixGenerator`/`CorpusFindingVerifier`'s own plan-wide
      `.Contains()` reads are unchanged — both are genuinely single-predicate
      probes where plan-wide and per-node are equivalent, exactly as this item
      anticipated. Regression coverage: a unit test feeding a synthetic
      two-branch plan XML directly to `ConvertImplicitDetector`
      (`ConvertImplicitDetectorTests.FindColumnConversions_TwoConversionsInSamePlanOnlyOneRangeBound_AttributesPerNodeNotPlanWide`)
      and a live Docker-oracle test running the actual UNION ALL query through
      `LiveScanRunner`
      (`LivePlanCacheReaderPerConversionAttributionOracleTests.RunAsync_OnePlanWithBothRangeSeekAndScanForcedConversions_AttributesEachColumnIndependently`
      in `tests/SilentScan.Tests/Integration/LivePlanCacheWorkloadFindingTests.cs`),
      both confirmed to fail against the pre-fix code. Not reachable against
      the local RM_ test database today (its plan cache never accumulates real
      application workload, since CLAUDE.md forbids ever executing scanned-
      target DML/procs) — the bug matters for real `scan-db` targets under
      live application traffic, where multi-predicate cached plans are the
      norm, not for this project's own local corpus target.
- [x] **Two shipped streams are missing from "Already shipped" above** — fixed,
      both now recorded there: the
      write-loss stream (`silentscan/write-loss/*`: unicode-to-non-unicode,
      approximate-to-exact truncation, numeric scale narrowing, temporal
      precision loss) and collation conflict
      (`silentscan/verdict/collation-conflict`, Msg 468, two real string
      columns with differing resolved collations and no explicit `COLLATE`).
      Both exist in code with rule IDs in `SarifRuleCatalog`. A backlog that
      under-reports what shipped is the same rot the narrative plan file died
      of; record them with the same detail as the other four entries.
- [x] **Fix the checklist's own numbering before it misleads anyone.**
      Resolved both open questions: (a) dropped the `### N.` numeric prefixes
      entirely in favor of descriptive headers used as slug-style anchors —
      numbers were insertion-order accidents that a future insert/delete would
      immediately stale again, while a descriptive header ("Join-key and
      cross-object type/collation mismatch") survives reordering the way a
      number can't, and every shipped rule already has its own real stable ID
      (`SargabilityFindingKind` etc.) for when precision actually matters; (b)
      folded "Candidates from the incumbent rule catalogs" and "Candidates
      from the wider product landscape" into Tier 1/2/3 by the same criteria
      (precision, catalog requirement, base rate) used everywhere else in this
      file — see each item's new home below. Two items didn't fold cleanly
      into a detection tier and got their own small sections instead: the two
      "measure a competitor before publication" items (not detections — put
      under a new "Research gates before publication" section), and the
      security/compliance rule-axis question, which is a scope decision this
      reorg pass deliberately did NOT make on its own (see "Open scope
      questions" below) since it would change what kind of tool this is, not
      just where a candidate rule lives.
- [x] **Record the runtime incumbent in `detection-reference.md`** — done, as
      Appendix 7 §7.7: a multi-platform commercial response-time analyzer
      whose entire SQL Server plan-advice surface is two XPath queries
      (missing index, unmatched index), and whose implicit-conversion
      detection is a plain `CONVERT_IMPLICIT` substring test with no notion
      of which side converted — a real shipping instance of the "greps for
      the marker without checking which side" failure mode this reference
      otherwise only posed hypothetically. Opens no detection gap on our
      side; its remaining analyses are runtime aggregates or DBMS-specific,
      already covered by the Tier 3 skips.

---

## Tier 1 — build next (high precision, needs our machinery, high base rate)

### Join-key and cross-object type/collation mismatch
Direct reuse of the precedence matrix on new predicate sites.

- [x] Type mismatch across JOIN `ON` columns — **already shipped**, discovered
      re-planning this item: `TypedPredicateExtractor.ExplicitVisit(QualifiedJoin)`
      already sets `Seekable` position over `node.SearchCondition` exactly like
      WHERE/HAVING, so every JOIN `ON` predicate already flows through the
      identical classification path. Oracle-tested:
      `TypedPredicateExtractorTests.JoinOnClausePredicate_IsResolved_OracleConfirmed`.
- [x] Proc/function **parameter type vs compared column type, inside the
      callee's own body** — **already shipped**: `RecordParameters` seeds a
      proc's declared parameters into the same variable-type dictionary a
      `DECLARE`d variable uses, so `WHERE Col = @Param` inside a proc body is
      indistinguishable from any other typed comparison to the existing
      pipeline. Oracle-tested:
      `TypedPredicateExtractorTests.VarcharColumnVsNVarcharParam_SqlCollation_ScanForced_OracleConfirmed`.
- [x] **Column collation ≠ database collation** — **shipped**:
      `Predicates/ColumnCollationDriftScanner.cs`, catalog-only, no AST
      walking. Wired into `ScanReport.ColumnCollationDriftFindings`
      (schema v7), SARIF rule `silentscan/catalog/column-collation-drift`,
      readable-report section. Also covers the **temp-table/table-variable
      vs. tempdb collation** half from the incumbent-catalog read in the same
      scanner (`DatabaseCatalog.EffectiveTempdbCollation`, `IsTempObject` on
      the finding) — the two were always the same catalog diff with a
      different baseline. Tested: 8 unit tests
      (`ColumnCollationDriftScannerTests`) covering fires/clean/unresolved-
      baseline/temp-object-baseline-fallback. Measured live against the
      local RM_ test database: 0 columns diverge there (its collation is
      uniform) — a real, honest zero; real-world drift (multi-tenant/
      migrated databases) is exactly what this catches, and its fixtures are
      directly authored (not internet-sourced) since a catalog-diff rule has
      no "bug repro" to cite the way a predicate rule does.
- [x] Cross-table same-name type drift report, **FK-linked half shipped**:
      `Catalog/ForeignKeyRelationship.cs` + `DatabaseCatalog.ForeignKeys`,
      populated live-only via `LiveCatalogReader` reading
      `sys.foreign_key_columns` (always empty in file mode, per CLAUDE.md's
      "everything goes via the database" rule — replicating FK DDL semantics
      would be reinventing the database-project wheel).
      `Predicates/CrossTableTypeDriftScanner.cs` flags a genuine category or
      collation difference (length/precision-only drift within the same
      category never fires — no conversion-seed story). Wired into
      `ScanReport.CrossTableTypeDriftFindings` (schema v8), SARIF rule
      `silentscan/catalog/cross-table-fk-type-drift`, readable-report
      section. **Oracle discovery, load-bearing for how this rule is
      described everywhere it's cited:** SQL Server structurally forbids a
      drifted FK from existing at all — `ALTER TABLE ADD CONSTRAINT` rejects
      a category *or collation* mismatch outright (Msg 1757, verified even
      `WITH NOCHECK`), and `ALTER COLUMN` on either side is blocked while the
      constraint exists (Msg 5074/4922). This means a currently-valid FK's
      two sides can **never** actually drift in a real, intact database —
      explaining why the local RM_ test database's real 1,247 FK pairs show
      0 drift (not empirical luck, a structural guarantee) — so the FK-linked
      half's real-world yield is essentially always zero on a healthy schema;
      its value is as a defensive/completeness check (a drifted FK could
      still exist behind an orphaned/disabled constraint this pass doesn't
      currently distinguish) rather than a real finding source. The scanner's
      own logic is proven correct directly against a hand-built catalog
      (`CrossTableTypeDriftScannerTests`, 4 tests — the only way to construct
      the state at all) plus a live oracle test locking in the negative
      (`LiveCatalogReaderTests.ReadAsync_MatchingForeignKey_CrossTableTypeDriftScannerNeverFires`).
      **Remaining, explicitly not shipped:** the join-candidate (no-FK,
      same-name-column) half — scoped to pairs with an actually-observed
      column-vs-column comparison somewhere in the scanned corpus, not a
      blanket same-name sweep across every table pair (an unqualified sweep
      has real false-positive risk: two unrelated tables that happen to both
      have a `Notes` column, never joined on, is noise). This is where the
      real-world value of this whole item now concentrates, given the FK-half
      finding above — needs genuinely new AST machinery (collecting every
      column-vs-column comparison in the corpus regardless of whether
      `VerdictClassifier` would itself flag it, unlike the existing typed-
      predicate pipeline which only records a finding when types actually
      differ). Of 1,053 same-named columns across ≥2 tables in the local RM_
      database, 83 have real sargability-relevant type/collation drift (29
      collation-specific) — the number to cite once the observed-comparison
      narrowing ships, not the old crude-heuristic "148" (the same live
      measurement reproduces "148" exactly when length/precision-only
      differences are also counted, confirming the old number was accurate
      but over-inclusive for what actually matters).
- [x] `sql_variant` in comparisons — **shipped**: one new branch pair in
      `VerdictClassifier.ClassifyWithReason`, before the existing
      `IsOutOfModelCategory` early-out — sql_variant is T-SQL's highest-
      precedence type, so unlike every other out-of-model category it
      participates cleanly in the standard "lower-precedence side converts"
      rule. Oracle-verified **both directions**: an indexed `int` column vs.
      a `sql_variant` value produces `Convert DataType="sql_variant"
      Implicit="1"` directly on the column's `ColumnReference`, an Index
      Scan with no `RangeColumns`/`GetRangeThroughConvert` anywhere
      (`ScanForced`); an indexed `sql_variant` column vs. an `int` value
      converts the *value* instead, with a genuine Index Seek and a real
      `SeekPredicates`/`RangeColumns` entry on the column (`SeekPreserved`).
      Two `sql_variant` operands, or `sql_variant` paired with another
      out-of-model category, correctly stay `Unknown` (execution-time boxed-
      type semantics, not statically resolvable). Tested: 4 unit tests
      (`VerdictClassifierTests`) + 2 live oracle tests
      (`TypedPredicateExtractorOracleTests`). 4 columns locally (confirmed
      accurate via live measurement, matching the existing figure).
- [x] **Call-boundary argument mismatch** — **shipped**: the value flowing
      INTO a parameter at a real `EXEC` call site has a different declared
      type than the parameter's own declaration (e.g. a `varchar` local
      variable passed into an `nvarchar` param). Doesn't itself lose a seek —
      EXEC parameter marshalling isn't a predicate — so it's classified with
      `Rules.WriteLossClassifier` (same family as `WriteLossFinding`) rather
      than `VerdictClassifier`'s seek/scan vocabulary. `ProcCallGraphBuilder`
      now tracks a per-scope variable-type dictionary (a variable's DECLARED
      type never changes after its own DECLARE, unlike its value, so this
      needs none of `ResolvePropagatedLiteral`'s reaching-definitions
      machinery — seeded once per scope from the scope's own formal
      parameters plus every DECLARE at any nesting depth) and stamps
      `ProcCallArgument.CallerArgumentType`. New
      `Predicates/ProcCallArgumentMismatchScanner.cs` walks the built graph
      and classifies each variable-reference argument. Ships as a standalone
      catalog+call-graph finding (`ProcCallArgumentMismatchFinding`) rather
      than chaining into a specific predicate finding inside the callee — the
      cross-referencing needed to chain provenance is real additional
      machinery, deliberately out of scope for this pass; revisit as a
      follow-up. No plan-XML oracle marker applies to a parameter binding the
      way `CONVERT_IMPLICIT`-on-column does to a predicate, so this rule has
      no plan-XML oracle fixture — the underlying silent-data-loss runtime
      behavior each `WriteLossKind` claims is already oracle-proven
      separately (`WriteLossOracleTests`, self-authored probe rows), so this
      stream's own tests only needed to prove the call-boundary WIRING: 12
      unit tests (`ProcCallGraphBuilderTests` for caller-type resolution
      including scope-isolation and enclosing-parameter cases,
      `ProcCallArgumentMismatchScannerTests` for the classifier) plus a live
      end-to-end oracle test through the real engine-authoritative pipeline
      (`ProcCallArgumentMismatchPipelineTests`) confirming a real deployed
      caller/callee pair surfaces the finding. Wired into
      `ScanReport.ProcCallArgumentMismatchFindings` (schema v9), SARIF rule
      `silentscan/call-graph/argument-type-mismatch`, readable-report
      section. Real-world fixtures for this exact phenomenon (as opposed to
      the in-body case, which is well-documented) are rare, so its own
      fixtures are directly authored rather than internet-sourced, per
      CLAUDE.md's rare-exception allowance.
- Oracle: identical `CONVERT_IMPLICIT`-on-column probe as the existing
  stream, except the call-boundary and catalog-only items above, which have
  no plan-XML oracle by construction (see each item).

**This entire section is now closed** — all five sub-rules shipped (two
turned out to already exist; three genuinely new), leaving only the explicit
follow-up under cross-table type drift (the join-candidate/observed-
comparison half).

### Type-aware upgrade of the sargability stream
Highest base rate of anything measured: ~1,100 modules with ISNULL/COALESCE
in WHERE clauses; 96 with RTRIM-family wrappers; 54 with leading wildcards.

- [ ] `ISNULL(col, x) = y` / `COALESCE(col, x) = y` in predicates — upgrade
      from syntactic flag to verdict: is the column nullable (catalog)? does
      an index exist? does COALESCE's result-type inference (highest
      precedence operand) flip a conversion onto the column? Folds in the
      incumbent-catalog finding "`ISNULL`/`COALESCE` arguments of differing
      datatypes" — the incumbent rule flags the mismatch but never computes
      the result type, so it can't say whether a conversion lands on the
      column; this item's own result-type inference is what makes it precise.
- [ ] Date-form non-sargables as named rules: `YEAR(col)=`, `DATEPART` on
      column, `DATEADD/DATEDIFF` on column, `CONVERT(varchar, col, n)`
      comparisons, BETWEEN with end-of-period boundary.
- [ ] `CHARINDEX(x, col)` / `LEFT(col, n) =` — rewritable-to-sargable forms.
- [ ] UPPER/LOWER on column **checked against actual collation** — fires only
      when the column's collation is case-sensitive (existing linters assume
      case-insensitivity blindly). Collation-aware = our edge.
- [ ] Index-existence weighting for all sargability findings: an unsargable
      predicate on an unindexed column is noise; on an indexed column it's a
      lost seek (we already rank expression findings by underlying index —
      extend to the whole stream).
- **Mandatory precision guard for every rule in this section:** a function
  wrapping a column does not imply a lost seek when an indexed computed column
  matches the same expression. Already landed for the shipped
  function-wrapped-column rule's own JSON_VALUE/JSON_QUERY case (see
  "Corrections to shipped work" above, `JsonComputedColumnMatcher`) — hold
  each new rule here to the same guard rather than re-deriving it.
- Oracle: seek-vs-scan probes; for the collation rule, CS vs CI fixtures.

### Oversized and MAX-typed parameters
Under-represented in existing rule sets, adjacent to the existing conversion
code, high precision.

- [ ] `varchar(max)`/`nvarchar(max)` parameter or variable compared to a
      `(n)`-typed column — blocks predicate pushdown even when the base type
      matches; no seek.
- [ ] Parameter declared longer than the compared column (`varchar(200)` param
      vs `varchar(50)` column) — memory-grant inflation; lower severity.
- [ ] MAX-typed columns used as predicate/join targets (can't be an index
      key) — catalog-only report.
- [ ] **A MAX-typed (or otherwise oversized) *comparison value* defeats a seek
      even against a matching indexed computed column** — oracle-found while
      landing the JSON_VALUE computed-column suppression above: comparing
      `JSON_VALUE(Payload, '$.status')` (matched to an indexed computed
      column) against an `NVARCHAR(MAX)` variable still forces a scan, even
      though a literal or bounded-length variable comparison seeks cleanly.
      Same underlying mechanism as this section's first two items; worth its
      own fixture pair once this section is picked up rather than being
      re-discovered from scratch.

### Join predicate incomplete vs. the backing foreign key
Folded in from the incumbent-catalog read — "strongest single find" there,
nobody resolves it properly.

- [ ] A join missing a backing FK entirely, or joining on fewer columns than
      a composite FK defines. The partial-composite case is a real
      correctness *and* plan defect (silent row multiplication); pure catalog
      work (walk `sys.foreign_key_columns` against the query's own join
      columns).

### SET options that silently disable plan features
Folded in from the incumbent-catalog read. Universally filed elsewhere as
style hygiene; the actual consequence is plan-shape, and it's
catalog-verifiable per module, not a guess.

- [ ] `QUOTED_IDENTIFIER OFF` and `NUMERIC_ROUNDABORT ON` mean **indexed
      views and filtered indexes cannot be used** by that module — catalog
      flag (`sys.sql_modules.uses_quoted_identifier`; `NUMERIC_ROUNDABORT` has
      no baked-in `sys.sql_modules` column the way `uses_quoted_identifier`/
      `uses_ansi_nulls` do, so that half needs the same syntax-only `SET`
      scan the ARITHABORT subrule below uses).
- [ ] **`ARITHABORT` subrule** — structurally different from the two above:
      purely a connection/session setting, invisible to catalog inspection of
      the object itself (verified: not one of `sys.sql_modules`'s baked-in
      settings). The only static surface is an explicit `SET ARITHABORT OFF`
      statement in the T-SQL text (syntax-only, same shape as the
      already-shipped FORCEPLAN/deprecated-SET-option rules). Real-world
      story: SSMS defaults the setting ON, most driver/app connections
      default it OFF, so the *same* proc gets different cached plans
      depending on which kind of connection first compiled it — classic
      "fast in SSMS, slow from the app." **Precision guard (mandatory):**
      only fire when the module's dependency graph actually touches a table
      with a filtered index or an indexed view (catalog-derivable via the
      same dependency walk the schema-scan UDF stream already does) — an
      explicit `SET ARITHABORT OFF` in a module that never touches either is
      noise. Oracle: compile/connect with ARITHABORT ON vs OFF against a
      query touching a filtered index and diff the plan or plan-cache entry.

---

## Tier 2 — strong candidates (precise rules exist, new machinery needed)

### Catch-all / kitchen-sink predicates
425 modules in the production copy (`... OR @param IS NULL`).

- [ ] `(col = @p OR @p IS NULL)` and COALESCE/ISNULL-disabled optional
      filters — one plan must serve all parameter combinations.
- [ ] **Precision guard (mandatory):** `OPTION(RECOMPILE)` on the statement
      largely neutralizes it — detect and either suppress or downgrade.
      70 modules locally already use OPTION(RECOMPILE).
- [ ] Sibling: parameter overwritten before use in a predicate
      (sniffing-defeat — straight-line dataflow we already have from
      dynamic-SQL tracing).

### Local-variable predicates
- [ ] `WHERE col = @v` where `@v` is DECLAREd in the batch (not a parameter) —
      density-vector estimate instead of sniffed value. Distinguishable from
      parameters purely in the AST; same OPTION(RECOMPILE) guard as the
      "Catch-all / kitchen-sink predicates" section above.

### NOT IN over a nullable subquery column
346 modules locally use `NOT IN (SELECT ...)`.

- [ ] Fires **only** when the catalog says the subquery column is nullable —
      correctness trap plus expensive null-aware anti-semi-join. The
      nullability gate is what makes this precise where linters spray.

### UPDATE ... FROM without source uniqueness
- [ ] Target joined to a source whose join columns carry no PK/unique
      constraint — nondeterministic multi-match update (each target row takes
      an arbitrary source row). Catalog-gated; correctness-first finding with
      a real perf angle (MERGE raises where UPDATE silently picks).

### Forced-serial construct inventory
- [ ] Table-variable **modification** (INSERT/UPDATE/DELETE @t) — whole plan
      serial; read-only use does not fire (direction-style distinction).
      821 modules locally use table variables.
- [ ] Dynamic/keyset cursors; cursor without `LOCAL FAST_FORWARD` as the
      crisp subrule (197 modules with cursors locally).
- [ ] The finite serial-forcing intrinsics list (IDENT_CURRENT, ERROR_NUMBER,
      @@TRANCOUNT, OBJECT_ID, ...) in queries — encodable, documented.
- [ ] Serial-zone constructs as informational: TOP row goals, recursive CTEs,
      MSTVF refs (already shipped — MSTVF-as-fence stream), global scalar aggregates.
- Note: several of these fold naturally into the already-shipped scalar UDF
  stream or the already-shipped MSTVF-as-fence stream rather than standing
  alone — decide at design time.

### Lineage-metric findings (cheap adds on existing passes)
- [ ] Nested-view depth report — we already compute topo order; emit depth ≥ N
      as a finding with the chain (57 views reference other views locally).
- [ ] Multi-referenced CTE — inline macro re-executed per reference; count
      references in the AST. Rarely covered anywhere; high precision.
- [ ] Untrusted (WITH NOCHECK) FK/CHECK constraints — optimizer forfeits join
      elimination; pure catalog flag (`is_not_trusted`).
- [ ] Cascading FK actions (ON DELETE/UPDATE CASCADE) — hidden serial
      multi-table work per DML; catalog-only, informational.

### Dynamic SQL quality (extends the existing dynamic-SQL pass)
123 modules use `EXEC(...)`, 51 use sp_executesql locally.

- [ ] Concatenated **value** (vs identifier) in proven-constant dynamic SQL —
      unparameterized: plan-cache pollution + per-literal compiles. We already
      prove constancy; classifying concatenation operands as value/identifier
      is incremental.
- [ ] `EXEC(string)` where sp_executesql with params was possible (only when
      the taint analysis shows a parameterizable value) — report, don't guess.
- [ ] **Temp-table shape mismatch across a proc-call boundary** — one small
      T-SQL type-checker in the wider tool landscape checks a version of this;
      ours would be catalog+plan-backed, not a type-inference heuristic. T-SQL
      scoping means a `#temp` table created by a calling proc stays visible to
      a proc it calls during that call, and `INSERT INTO #temp EXEC OtherProc`
      assumes the executed proc's result-set shape matches `#temp`'s declared
      columns — silently wrong column count/order/type is either a runtime
      error or, worse, a silent per-column implicit conversion that our
      existing conversion stream would never see because it never runs on the
      `EXEC`'d proc's own SELECT list against the caller's temp-table DDL. This
      is squarely in-scope by CLAUDE.md's own boundary: temp tables created
      inside proc bodies are explicitly one of the module-body objects we
      already parser-model (the engine doesn't expose them). Ground truth for
      the executed proc's actual shape: `sys.dm_exec_describe_first_result_set`
      (compile-only, same guarantee the `scan-db` describe-only path already
      relies on) rather than re-deriving it from the proc's own SELECT list.
      Verdict: column-position type mismatch between the temp table's DDL and
      the executed proc's described result set. Oracle: compile-only probe of
      the `INSERT ... EXEC` batch under SHOWPLAN_XML, same discipline as the
      shipped streams.

### Schema-scan UDF and computed-column findings (found on completeness audit)
Distinct trigger from the already-shipped scalar UDF stream's plan-based
findings: these fire from catalog
metadata alone, independent of whether the object ever shows up in a cached
plan, so they need no plan/oracle involvement to report (though should still
get an oracle fixture for the serial-plan consequence).

- [ ] CHECK constraint whose definition references a scalar/CLR function —
      forces serialized execution of every query and maintenance operation
      against the table. Pure catalog scan (`sys.check_constraints`
      definition text against `sys.objects` function list).
- [ ] Non-persisted computed column (`is_persisted = 0`), independent of
      whether it references a UDF — recomputed on every read; broader trigger
      than the UDF-specific rule in the already-shipped scalar UDF stream,
      catalog-only.
- [ ] Deprecated `*=`/`=*` outer-join operators — legacy syntax that can
      silently change join semantics and plan shape across engine versions;
      pure AST syntax check, near-zero FP risk, cheap to add.

### Halloween Protection and self-referencing DML
- [ ] `INSERT`/`UPDATE`/`DELETE`/`MERGE` whose source query reads the same
      target table (hole-filling `INSERT ... WHERE NOT EXISTS`,
      `UPDATE ... FROM` self-join) — forces a blocking eager spool, distinct
      mechanism from the UDF-in-DML case already covered. Pure syntax: target
      table object also appears in the statement's read-side FROM/subquery.

### Temporal table history-side index gap
- [ ] System-versioned temporal table (`sys.tables.temporal_type`) whose
      history table lacks the index set the current table has — `FOR
      SYSTEM_TIME AS OF/BETWEEN` queries rewrite to a UNION ALL between the
      two tables, so a sargable predicate on the current side does nothing
      for the history side, silently forcing a scan on half the union.
      Catalog-only: compare index definitions between `parent_id` and
      `history_table_id`.

### Small precise adds (each an afternoon, not a stream)
- [ ] Proc authored `WITH RECOMPILE` — compiles every call, invisible to
      cache-based monitoring; pure catalog flag (`sys.sql_modules`).
- [ ] `RANGE` instead of `ROWS` in window-function frames — on-disk spool per
      partition; purely syntactic, near-zero FP risk.
- [ ] Trigger content scan — run trigger bodies through the existing pipeline
      so cursors/UDFs/MSTVFs inside triggers surface as hidden per-DML cost
      (the modules are already in `sys.sql_modules`; this is mostly wiring).
- [ ] `COMPUTE`/`COMPUTE BY` deprecated aggregate constructs — bypasses
      normal set-based aggregate optimization; syntax-only, rare but trivial.
- [ ] `WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch — holds a
      worker thread idle, contributing to worker exhaustion under load;
      syntax-only.
- [ ] Transaction hygiene pair: lengthy work (loops, RBAR, external calls)
      between an error and its `ROLLBACK` extends lock hold duration;
      `BEGIN TRANSACTION` with no reachable `ROLLBACK`/`COMMIT` on some path
      leaves locks held indefinitely. Both are control-flow/dataflow checks
      over the AST, no catalog needed.
- [ ] **`TOP(100) PERCENT` ignored by the optimizer** and **`ORDER BY` in a
      view / inline TVF** (folded in from the incumbent-catalog read) — same
      family, both commonly written to "force" ordering that is not
      guaranteed. Syntactic, near-zero FP.
- [ ] **`IF` statements containing queries inside a procedure** (folded in
      from the incumbent-catalog read) — estimation and recompile
      consequences; nobody frames it as a performance finding.

### Hint and index-shape catalog checks
Folded in from the incumbent-catalog read (`detection-reference.md` Appendix
7) — both need our catalog and neither is done properly anywhere surveyed.

- [ ] **Hint validity against the catalog** — every surveyed tool flags
      `INDEX`/`JOIN`/`TABLE`/`QUERY` hints as a *style* smell ("avoid hints").
      The catalog-requiring version nobody does: an `INDEX(...)` hint naming an
      index that **no longer exists**, or pinning an index the predicate cannot
      seek anyway, so the hint forces a scan of the wrong index.
- [ ] **Composite index leading-column violation** — predicate filters a
      non-leading key column while the leading column is unconstrained. One
      surveyed tool has this as a regex; against real index key ordering it
      becomes precise. Scope it as a *predicate* finding ("this query cannot
      seek this index"), never as an index recommendation, or it drifts into
      the index-advisor skip.

---

## Research gates before publication (not detections)
Two items from the wider-landscape/incumbent-catalog reads that are measurement
tasks, not rule candidates — they don't belong in a detection tier, but need
doing before the study can make certain public claims.

- [ ] **Pre-publication gate: measure the second type-binding incumbent's
      conversion rule against our direction fixtures.** The commercial
      schema-bound analyzer previously recorded as dead is in fact alive, and
      its cross-type-operator rule is genuinely connection-bound. Its docs
      read as symmetric, but that's an unverified negative (vendor site
      defeats fetching). Trial-install it and run the same three-case demo
      used against `SRP0016`; the study cannot claim "nothing is
      direction-aware" in public until this is measured.
- [ ] **Follow-up gate, same shape as the pre-publication gate above: two new
      tools found need a closer look before being ruled out.** One
      (Rust, WASM-delivered, ~103 T-SQL rules, actually issues `SET PARSEONLY`
      and rollback-plan probes — the closest oracle discipline to ours found
      anywhere; actively maintained) and one distributed via
      NuGet (169 rules across security/correctness/performance/convention,
      actively published). Docs for both show no implicit-conversion hit on a
      grep, but that's from docs, not a source read — same caveat class as the
      pre-publication gate item. Lower priority than that one since neither
      claims schema-binding, but cheap to close: read their rule source before
      the study cites "nothing else exists" as settled.

---

## Tier 3 — deliberate skips (decided; don't re-litigate without new evidence)

- **Parameter sniffing** — runtime data-distribution problem; static tools can
  only flag risk factors → hedged findings violate the precision bar.
- **SELECT \*, SET NOCOUNT, sp_ prefix, schema-prefix, ORDER BY ordinal,
  style/correctness linting** — crowded syntax-only territory several linters
  already cover; diluting into a generic linter destroys the tool's identity.
  (Missing schema prefix and unparameterized ad-hoc SQL are the only ones
  with real perf teeth, and both are plan-cache problems, not plan-shape
  problems.)
- **Missing/duplicate/unused indexes, heaps, fill factor, clustering-key
  width** — index-advisor space; catalog-only, no query analysis, different
  tool.
- **Runtime-only signals** — spills, memory grants, execution frequency,
  compile time, stale stats, plan-cache duplication, row-estimate mismatch:
  by definition not static. The oracle stays compile-only.
- **NOLOCK / READ UNCOMMITTED** — correctness smell wearing a performance
  costume; only 17 modules locally; linters cover it.
- **MERGE pitfalls** — real but correctness-focused and version-dependent;
  19 modules locally. Revisit only if a precise perf-framed subrule emerges
  (`WHEN MATCHED THEN DELETE`, missing HOLDLOCK).
- **CHECK-constraint-as-enum dead predicate** (from the wider-product-landscape
  read: one small PoC-labeled T-SQL type-checker treats `CHECK (col IN (...))`
  as an implicit enum and flags a predicate comparing the column against a
  literal proven outside it) — *decided, folding the checklist-numbering
  item's open question.* This is fundamentally a correctness finding (an
  always-false predicate), the same family as the incumbents' "comparison
  always evaluates to TRUE/FALSE" rule, which they themselves file under
  Design, not Performance. The one sibling correctness-adjacent finding kept
  on this backlog — NOT IN over a nullable subquery column, above — was kept
  specifically because it has a real perf angle beyond the correctness bug
  (an expensive null-aware anti-semi-join); this candidate has no equivalent
  extra angle, so it gets the same treatment as MERGE pitfalls just above:
  skipped unless a genuinely perf-framed variant turns up.
- **DISTINCT-masking-bad-join, correlated-subquery-won't-unnest, row goals,
  UNION vs UNION ALL** — harm depends on optimizer decisions we'd be
  guessing; low-precision by nature. Inventory-grade at best.
- **Indexed view NOEXPAND matching** — edition-dependent and matching-logic
  FP risk; revisit if the corpus shows indexed views at all.
- **OR across different columns** — detection trivial, harm imprecise
  (index union often fine). Only viable as an index-aware variant; parked.
- **Partition elimination defeat** (non-literal/wrapped predicate on the
  partitioning column) — real and distinct from b-tree seek/scan, but needs
  partition function/scheme catalog modeling we don't have yet. Revisit if a
  corpus repo turns out to use partitioning.
- **Always Encrypted column-comparison restrictions** — high precision in
  principle (catalog exposes `encryption_type`), but needs a target using the
  feature to matter at all; the local production copy and pilot corpus don't.
  Revisit if that changes.
- **Batch Mode on Rowstore eligibility loss** — deterministic in principle,
  but Microsoft doesn't publish a fully canonical disqualifier list, so
  completeness (and therefore precision) is a real risk; parked until a
  trustworthy exhaustive list exists.
- **Window-function POC (Partition-Order-Covering) index shape** — real
  (missing index keyed PARTITION BY → ORDER BY → covering columns forces a
  Sort per partition), catalog + syntax detectable, but scoped as an
  index-advisor recommendation rather than a query-defect finding; revisit if
  the tool's scope ever grows to include index suggestions.
- **Query/order hint usage counters** (`sys.dm_exec_query_optimizer_info`
  join/order hint frequency) — inherently a runtime aggregate (counts since
  last restart), not a per-query static fact; the static form is already
  covered by the hard-coded-hints skip above.
- **SonarQube T-SQL rule coverage** — *resolved, no longer open.*
  The free-tier SonarQube T-SQL path is a community plugin, read at source: 16
  enabled T-SQL rules, all declarative ANTLR parse-tree shape matches, dormant
  since 2024, no implicit-conversion rule of any kind. Its non-sargability rule
  is `BETA` with no ground truth. The CI-gate niche is effectively unoccupied.
  Details in `detection-reference.md` → "Named incumbents".

---

## Open scope questions
Genuinely undecided, unlike Tier 3 above — these change what *kind* of tool
this is rather than just adding a rule, so they're kept separate from both the
detection tiers and the deliberate-skip list rather than defaulted either way.

- **Security, compliance, and correctness-only rules as a whole new axis.**
  The incumbent catalogs carry a substantial security axis (one tool devotes
  61 rules to it — its second-largest category — and the actively-developed
  DacFx pack ships a SQL-injection rule). Adding that axis would mean this
  tool detects a bug class CLAUDE.md's own identity statement doesn't mention
  at all (type-aware, direction-aware, lineage-aware *performance* analysis) —
  a scope call for whoever owns that identity, not something a checklist
  reorg should decide by default. The full incumbent security rule lists are
  in `detection-reference.md` Appendix 7 §7.4 so the decision can be made from
  the actual rules rather than a category label. Note the one already-admitted
  overlap: `EXEC(string)` where `sp_executesql` was possible (Tier 2's
  "Dynamic SQL quality" section) is admitted purely on plan-cache grounds,
  with the injection surface as a side effect, not as security scope creep.

## Reporting ideas worth stealing (not detections)
Folded in from the wider-product-landscape read — cross-cutting reporting/UX
ideas, not rule candidates, so they don't belong in a detection tier.

- [ ] **Confidence tiers as a first-class output axis** — one surveyed tool
      gates findings Proven/Contextual/Advisory with a CI-safe default. Maps
      onto our `Verdict` + `Unknown` split and the "static-only findings go in
      an appendix" rule; would make the SARIF export a safer CI gate.
- [ ] **Source-context classification** (migration/deployment script vs
      hot-path module) used to filter before reporting — a one-off deployment
      script legitimately does things a proc must not.
- [ ] **Machine-readable rule catalog generated from the rule types**, carrying
      id/severity/rationale/examples/fix-guidance, feeding both docs and the
      SARIF `rules` block — keeps documentation and code from drifting apart.

---

## Cross-cutting requirements for every new stream

- Verdict-bearing rules ship an oracle fixture (compile-only SHOWPLAN_XML);
  syntactic-only rules ship fire + near-miss fixtures from real,
  internet-sourced bugs (no invented repros).
- Findings carry the same schema as conversions: verdict, indexed?, depth,
  origin (predicate site + introducing layer), machine-readable reasons.
- Every rule states its engine-version sensitivity (2017 interleaved
  execution, 2019 UDF inlining, 2022 CE behavior) rather than assuming one.
- Deterministic ordering; `Unknown` over guesses; unanalyzable counts
  reported honestly.
- The study angle for each shipped stream: prevalence at lineage depth ≥ 1
  ("inherited through views/TVFs") is the number nobody else can produce.
