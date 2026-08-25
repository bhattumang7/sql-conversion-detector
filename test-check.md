Rules for each test:

1. CLAIM: one sentence stating the specific engine behavior this test proves (from the class/method doc + the rule it backs — e.g. RuleCatalog/detection-reference.md — not guessed from the test name).

2. MUTATION CHECK: if the behavior were broken/deleted, would this test actually fail? Assert.NotNull, Assert.True(count > 0), and broad Assert.Contains against a large blob are red flags — state what a broken implementation would still make the test print.

3. SELF-MATCH RISK: could the test's own query match itself (its own compiled plan, its own DDL, a leftover row from another test in the same shared instance)? Plans are cached at compile time — a query can see itself.

4. RIGHT ROW, NOT A ROW: if a query can return multiple matching rows (ad-hoc + prepared cache entries, multiple compat-level plans, multiple name-matched objects), does the test pin down which row demonstrates the claim, or just read once and trust it?

5. CONTROL/NEGATIVE CASE: is there a sibling case that should NOT show the effect, checked in the same run/statement (per ForcedParameterizationScanner's same-statement isolation pattern)? Positive-only tests can't rule out "this just always happens here."

6. RIGHT ARTIFACT: for plan-SHAPE claims (operator, seek vs scan, CONVERT_IMPLICIT), does the test read SHOWPLAN_XML/actual plan attributes — not cached SQL text or a catalog view that's merely correlated? Conversion findings: oracle is plan-XML based, never plan-shape based.

7. TEXT-FORMAT BRITTLENESS: for normalized/cached-SQL-text assertions, could exact spacing/casing/punctuation assumptions (e.g. "dbo.T" vs "dbo . T") cause silent over- or under-matching? Verify by actually running one, don't assume existing tests got it right.

New scanners with type/narrowing logic must route through Rules/WriteLossClassifier,
Rules/NumericFamilyNarrowing, TypeInference/ExpressionTypeInferencer (via
Lineage/ScalarExpressionResolver, not directly), or ScopedSqlVisitorBase's own
ResolveColumnFacts/CurrentResolutionContext - never hand-roll a resolveLeaf closure or a
parallel scope-resolution helper. See tests/SilentScan.Tests/Architecture/TypeInferenceConventionTests.cs.
