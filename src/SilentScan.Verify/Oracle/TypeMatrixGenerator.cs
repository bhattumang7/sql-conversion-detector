using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Regenerates the checked-in oracle-probed type-pair matrix consumed by
/// <c>SilentScan.Core.Rules.TypePairMatrix</c>. For
/// every category pair in a family, deploys an indexed single-column table per category and
/// probes a column-vs-parameter comparison compile-only under SHOWPLAN_XML, recording whether
/// the column side converts, whether a dynamic range seek is available, or whether the pair
/// does not compile at all. Re-run this (via `silentscan-verify generate-type-matrix`) whenever
/// the Docker SQL Server image version changes, since these are empirical facts about a specific
/// optimizer build, not derived from the T-SQL precedence list.
/// </summary>
public sealed class TypeMatrixGenerator
{
    /// <summary>Numeric-or-bit family category/DDL-syntax pairs probed against each other (all ordered pairs, both directions).</summary>
    public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> NumericFamily =
    [
        (SqlTypeCategory.Bit, "BIT"),
        (SqlTypeCategory.TinyInt, "TINYINT"),
        (SqlTypeCategory.SmallInt, "SMALLINT"),
        (SqlTypeCategory.Int, "INT"),
        (SqlTypeCategory.BigInt, "BIGINT"),
        (SqlTypeCategory.SmallMoney, "SMALLMONEY"),
        (SqlTypeCategory.Money, "MONEY"),
        (SqlTypeCategory.Decimal, "DECIMAL(18,4)"),
        (SqlTypeCategory.Real, "REAL"),
        (SqlTypeCategory.Float, "FLOAT"),
    ];

    /// <summary>Date/time family category/DDL-syntax pairs.</summary>
    public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> DateTimeFamily =
    [
        (SqlTypeCategory.Time, "TIME"),
        (SqlTypeCategory.Date, "DATE"),
        (SqlTypeCategory.SmallDateTime, "SMALLDATETIME"),
        (SqlTypeCategory.DateTime, "DATETIME"),
        (SqlTypeCategory.DateTime2, "DATETIME2"),
        (SqlTypeCategory.DateTimeOffset, "DATETIMEOFFSET"),
    ];

    /// <summary>
    /// Roadmap Phase A3: BINARY/VARBINARY/TIMESTAMP(rowversion) were previously absent from the
    /// matrix entirely - any comparison involving one resolved Unknown for lack of probe data,
    /// even though same-category comparisons (binary vs binary) already worked fine through
    /// VerdictClassifier's category-equality branch, which never consults the matrix. What
    /// actually needed probing is the cross-category case: a `binary(n)` column compared
    /// against a `varbinary(n)` value or vice versa, and a `timestamp`/`rowversion` concurrency
    /// column compared against a `varbinary(8)` variable - both real, common patterns (rowversion
    /// columns are a ubiquitous optimistic-concurrency idiom) that previously reported Unknown
    /// purely because nobody had probed them yet.
    /// </summary>
    public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> BinaryFamily =
    [
        (SqlTypeCategory.Binary, "BINARY(16)"),
        (SqlTypeCategory.VarBinary, "VARBINARY(16)"),
        (SqlTypeCategory.Timestamp, "ROWVERSION"),
    ];

    /// <summary>String-family category/DDL-syntax pairs, probed once per <see cref="Collations"/> entry.</summary>
    public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> StringFamily =
    [
        (SqlTypeCategory.Char, "CHAR(40)"),
        (SqlTypeCategory.VarChar, "VARCHAR(40)"),
        (SqlTypeCategory.NChar, "NCHAR(40)"),
        (SqlTypeCategory.NVarChar, "NVARCHAR(40)"),
    ];

    /// <summary>
    /// The collation families CLAUDE.md's type rules distinguish: legacy SQL_* (no dynamic
    /// range seek) vs Windows (dynamic range seek available) - probed across more than one
    /// representative of the Windows family specifically, since
    /// SilentScan.Core.Rules.TypePairMatrix.TryGetOutcomeAgreeingAcrossCollations generalizes "every
    /// probed collation agreed" from however many entries are here: two representatives is a
    /// thin basis for that claim (an audit finding - the two-collation set couldn't rule out a
    /// UTF-8 or binary collation disagreeing). A UTF-8 collation (common on SQL Server 2019+
    /// deployments, Collation.IsWindowsFamily currently classes it as Windows-family since it
    /// doesn't start with "SQL_") and a _BIN2 binary collation (byte-order comparison semantics,
    /// also non-"SQL_"-prefixed) are added specifically to stress that classification - both
    /// were verified empirically (against this same Docker oracle) to behave like the existing
    /// Windows-family representative (GetRangeThroughConvert present) before being added here,
    /// so their inclusion is expected to strengthen the existing agreement rather than surface a
    /// disagreement, but the matrix now has the probe density to actually detect one if a future
    /// SQL Server build changes that.
    /// </summary>
    public static readonly IReadOnlyList<string> Collations =
    [
        "SQL_Latin1_General_CP1_CI_AS",
        "Latin1_General_CI_AS",
        "Latin1_General_100_CI_AS_SC_UTF8",
        "Latin1_General_BIN2",
    ];

    /// <summary>
    /// Non-string representatives probed against every <see cref="StringFamily"/> category in
    /// both directions (string column vs numeric/datetime/guid/binary value, and vice versa) -
    /// the classic "varchar column vs int/date parameter" bug class the blanket
    /// precedence-list-only heuristic used to guess at instead of probing. The three
    /// <see cref="BinaryFamily"/> entries at the end are real, oracle-confirmed-comparable
    /// pairs (a rowversion/binary/varbinary column or value against a string, e.g. a hex-encoded
    /// rowversion literal compared against the real column) - previously entirely unprobed
    /// against strings even though every OTHER category here was.
    /// </summary>
    public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> CrossFamilyOther =
    [
        (SqlTypeCategory.Bit, "BIT"),
        (SqlTypeCategory.TinyInt, "TINYINT"),
        (SqlTypeCategory.SmallInt, "SMALLINT"),
        (SqlTypeCategory.Int, "INT"),
        (SqlTypeCategory.BigInt, "BIGINT"),
        (SqlTypeCategory.SmallMoney, "SMALLMONEY"),
        (SqlTypeCategory.Money, "MONEY"),
        (SqlTypeCategory.Decimal, "DECIMAL(18,4)"),
        (SqlTypeCategory.Real, "REAL"),
        (SqlTypeCategory.Float, "FLOAT"),
        (SqlTypeCategory.Time, "TIME"),
        (SqlTypeCategory.Date, "DATE"),
        (SqlTypeCategory.SmallDateTime, "SMALLDATETIME"),
        (SqlTypeCategory.DateTime, "DATETIME"),
        (SqlTypeCategory.DateTime2, "DATETIME2"),
        (SqlTypeCategory.DateTimeOffset, "DATETIMEOFFSET"),
        (SqlTypeCategory.UniqueIdentifier, "UNIQUEIDENTIFIER"),
        (SqlTypeCategory.Binary, "BINARY(16)"),
        (SqlTypeCategory.VarBinary, "VARBINARY(16)"),
        (SqlTypeCategory.Timestamp, "ROWVERSION"),
    ];

    private readonly DatabaseProvisioner _provisioner;
    private readonly ScriptDeployer _deployer;
    private readonly PlanXmlCapture _capture;

    public TypeMatrixGenerator(SqlServerOptions options)
    {
        _provisioner = new DatabaseProvisioner(options);
        _deployer = new ScriptDeployer(options);
        _capture = new PlanXmlCapture(options);
    }

    /// <summary>
    /// Probes <paramref name="numericFamily"/>, <paramref name="dateTimeFamily"/>, and
    /// <paramref name="binaryFamily"/> (none have a collation dimension) plus
    /// <paramref name="stringFamily"/> under every entry of <paramref name="collations"/>, and
    /// returns the combined outcome list plus the server build string extracted from the first
    /// successfully-captured plan.
    /// </summary>
    public async Task<(IReadOnlyList<TypePairProbeResult> Entries, string ServerVersion)> GenerateAsync(
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> numericFamily,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> dateTimeFamily,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> stringFamily,
        IReadOnlyList<string> collations,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)>? crossFamilyOther = null,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)>? binaryFamily = null,
        CancellationToken cancellationToken = default)
    {
        crossFamilyOther ??= [];
        binaryFamily ??= [];
        // Concurrent: ProbePairsAsync below runs up to MaxConcurrentProbes probes at once, each
        // recording its own result into this same bag.
        var entries = new System.Collections.Concurrent.ConcurrentBag<TypePairProbeResult>();
        string? serverVersion = null;

        // Every database name this method provisions carries this call's own unique suffix -
        // without it, two GenerateAsync calls running concurrently (the real, reproducible case:
        // TypeMatrixGeneratorTests and TypePairMatrixLiveRegenerationTests are separate xUnit
        // test classes, so xUnit's default parallelization runs them in different threads at the
        // same time) both provision the exact same literal database name ("SilentScanTypeMatrixFamily")
        // and race each other's CREATE/DROP - one call's DROP (which SET SINGLE_USER WITH
        // ROLLBACK IMMEDIATEs first) can kill the OTHER call's still-in-flight session outright
        // ("Cannot continue the execution because the session is in the kill state"), not just a
        // transient DMV-decode hiccup. A previous fix here tried widening a plan-cache reader's
        // retry budget instead - that only papered over one symptom of this same deterministic
        // naming collision, not the collision itself.
        var runSuffix = "_" + Guid.NewGuid().ToString("N")[..8];

        var needsCrossFamilyProbing = crossFamilyOther.Count > 0 && stringFamily.Count > 0;
        if (numericFamily.Count > 0 || dateTimeFamily.Count > 0 || binaryFamily.Count > 0 || needsCrossFamilyProbing)
        {
            var familyDb = "SilentScanTypeMatrixFamily" + runSuffix;
            SqlConnection.ClearAllPools();
            await _provisioner.CreateFreshAsync(familyDb, cancellationToken: cancellationToken);
            try
            {
                var baseFamily = numericFamily.Concat(dateTimeFamily).Concat(binaryFamily).ToList();
                await DeployFamilyTablesAsync(familyDb, baseFamily, collationName: null, cancellationToken);
                var familyContext = new ProbeContext(familyDb, CollationName: null, entries, v => Interlocked.CompareExchange(ref serverVersion, v, null), cancellationToken);

                // Cross-probed as ONE combined family, not three separate within-family-only
                // passes (numeric vs numeric, date/time vs date/time, binary vs binary) - every
                // pair across numeric/date-time/binary needs its own real probe too (an int
                // column vs a date value, a bit column vs a datetime value, a rowversion column
                // vs a decimal value, ...), not just same-family pairs. All three families are
                // already deployed together into this one database (baseFamily above), so this
                // costs nothing extra to deploy - it was previously just unprobed, not
                // unprobeable. Closes the single largest slice of `no-probed-matrix-cell`
                // Unknowns found auditing a real production database's scan.
                await ProbeFamilyAsync(baseFamily, familyContext);

                // Non-string column vs a string-typed value (e.g. `WHERE IntColumn = '123'`) is
                // not collation-sensitive - the target isn't a string, so which collation family
                // the string literal notionally belongs to has no bearing on whether the column
                // converts. Probe this direction exactly once (default collation, recorded as
                // CollationName=null to match how VerdictClassifier looks up non-string columns)
                // rather than once per collation family, which would both waste probes and
                // collide on the matrix's (Column, Other, Collation) dictionary key.
                if (needsCrossFamilyProbing)
                {
                    // crossFamilyOther is probed here as the COLUMN side (ProbeOnePairAsync's
                    // first argument), not just as a value type - every one of its categories
                    // needs its own dbo.T_(category) table, not only the ones that happen to
                    // already be deployed via numeric/dateTime/binary family membership. Missing
                    // this (UniqueIdentifier is the one CrossFamilyOther entry with no family of
                    // its own) previously left T_UniqueIdentifier undeployed, every guid-as-column
                    // probe threw "Invalid object name", and the previous blanket exception
                    // handler below recorded that as CompileFailed=true - a fabricated
                    // OperandClash for a pair that actually converts and seeks fine.
                    var missingColumnTables = crossFamilyOther
                        .Where(cf => !baseFamily.Any(b => b.Category == cf.Category))
                        .ToList();
                    if (missingColumnTables.Count > 0)
                    {
                        await DeployFamilyTablesAsync(familyDb, missingColumnTables, collationName: null, cancellationToken);
                    }

                    await DeployFamilyTablesAsync(familyDb, stringFamily, collationName: null, cancellationToken);
                    await ProbePairsAsync(
                        crossFamilyOther.SelectMany(other => stringFamily.Select(str => (other.Category, str.Category, str.Syntax))),
                        familyContext);
                }
            }
            finally
            {
                await _provisioner.DropIfExistsAsync(familyDb, cancellationToken);
                SqlConnection.ClearAllPools();
            }
        }

        foreach (var collation in collations)
        {
            if (stringFamily.Count == 0)
            {
                break;
            }

            var stringDb = "SilentScanTypeMatrixStr_" + SanitizeForIdentifier(collation) + runSuffix;
            await _provisioner.CreateFreshAsync(stringDb, collationName: collation, cancellationToken: cancellationToken);
            try
            {
                await DeployFamilyTablesAsync(stringDb, stringFamily, collation, cancellationToken);
                var stringContext = new ProbeContext(stringDb, collation, entries, v => Interlocked.CompareExchange(ref serverVersion, v, null), cancellationToken);
                await ProbeFamilyAsync(stringFamily, stringContext);

                // String column vs a non-string value: collation-sensitive because it decides
                // whether a resulting dynamic seek is available, so this direction IS probed
                // once per collation family.
                if (crossFamilyOther.Count > 0)
                {
                    await DeployFamilyTablesAsync(stringDb, crossFamilyOther, collationName: null, cancellationToken);
                    await ProbePairsAsync(
                        stringFamily.SelectMany(str => crossFamilyOther.Select(other => (str.Category, other.Category, other.Syntax))),
                        stringContext);
                }
            }
            finally
            {
                await _provisioner.DropIfExistsAsync(stringDb, cancellationToken);
                SqlConnection.ClearAllPools();
            }
        }

        return (entries.ToList(), serverVersion ?? "unknown");
    }

    private async Task DeployFamilyTablesAsync(
        string database, IReadOnlyList<(SqlTypeCategory Category, string Syntax)> family, string? collationName, CancellationToken cancellationToken)
    {
        var script = new System.Text.StringBuilder();
        foreach (var (category, syntax) in family)
        {
            var collateClause = collationName is null ? string.Empty : $" COLLATE {collationName}";
            script.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"CREATE TABLE dbo.T_{category} (Col {syntax}{collateClause} NOT NULL); CREATE INDEX IX_T_{category} ON dbo.T_{category}(Col);");
            script.AppendLine("GO");
        }

        await _deployer.DeployAsync(script.ToString(), database, cancellationToken);
    }

    /// <summary>
    /// Caps how many <see cref="PlanXmlCapture"/> probes run concurrently against the one Docker
    /// SQL Server instance. Each probe is its own connection/compile-only round trip with no
    /// shared mutable server-side state (verified: <see cref="PlanXmlCapture"/> holds no fields
    /// beyond the immutable <see cref="SqlServerOptions"/>), so probes are safe to run
    /// concurrently. Kept modest rather than matching the host's full core count: this generator
    /// itself runs from inside one xUnit test that is ALREADY one of many tests xUnit runs
    /// concurrently across every CPU core - stacking a wide degree of parallelism on top of an
    /// already fully-subscribed test run oversubscribes the same cores twice and, measured
    /// directly against the full suite (2026-08-19), made total suite time worse, not better,
    /// even though it sped up this one test in isolation.
    /// </summary>
    private const int MaxConcurrentProbes = 4;

    private Task ProbeFamilyAsync(IReadOnlyList<(SqlTypeCategory Category, string Syntax)> family, ProbeContext context) =>
        ProbePairsAsync(
            family
                .SelectMany(column => family.Select(other => (column.Category, other.Category, other.Syntax)))
                .Where(p => p.Item1 != p.Item2),
            context);

    /// <summary>Runs every (columnCategory, otherCategory, otherSyntax) probe in <paramref name="pairs"/> concurrently, up to <see cref="MaxConcurrentProbes"/> at once.</summary>
    private async Task ProbePairsAsync(
        IEnumerable<(SqlTypeCategory ColumnCategory, SqlTypeCategory OtherCategory, string OtherSyntax)> pairs, ProbeContext context)
    {
        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentProbes, CancellationToken = context.CancellationToken },
            (pair, ct) => new ValueTask(ProbeOnePairAsync(pair.ColumnCategory, pair.OtherCategory, pair.OtherSyntax, context with { CancellationToken = ct })));
    }

    /// <summary>
    /// SQL Server error numbers this generator treats as real CompileFailed=true facts about a
    /// type pair, empirically observed from the four distinct compile errors SQL Server actually
    /// raises for "these two types cannot be compared at all": 206 ("Operand type clash: %ls is
    /// incompatible with %ls", e.g. uniqueidentifier vs a numeric/datetime type), 402 ("The
    /// data types %ls and %ls are incompatible in the %ls operator", e.g. time vs date/datetime),
    /// and a pair surfaced once cross-family probing was widened to include timestamp/rowversion
    /// vs string (char/varchar to timestamp specifically refuses even an implicit attempt, unlike
    /// every other cross-family pair this generator had probed before) - 257 ("Implicit
    /// conversion from data type %ls to %ls is not allowed. Use the CONVERT function to run this
    /// query", the VALUE-side direction: a string value compared against a timestamp column) and
    /// 260 ("Disallowed implicit conversion from data type %ls to data type %ls, table '%ls',
    /// column '%ls'. Use the CONVERT function to run this query", the COLUMN-side direction: a
    /// string COLUMN compared against a timestamp value - a distinct error number specifically
    /// because it names the offending column, not just the two types).
    /// Deliberately NOT a blanket `catch (SqlException)`: that previously swallowed error 208
    /// ("Invalid object name") from a probe table this generator forgot to deploy
    /// (dbo.T_UniqueIdentifier) and recorded a fabricated CompileFailed=true for a pair that
    /// actually converts and seeks fine. Any SqlException whose number isn't in this set is a bug
    /// in the generator or its deployment, not an empirical fact about the type pair, and must
    /// fail the run loudly instead of being recorded as a wrong verdict.
    /// </summary>
    private static readonly HashSet<int> TypeIncompatibilityErrorNumbers = [206, 257, 260, 402];

    private async Task ProbeOnePairAsync(SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, string otherSyntax, ProbeContext context)
    {
        var probe = $"DECLARE @p {otherSyntax}; SELECT Col FROM dbo.T_{columnCategory} WHERE Col = @p;";
        try
        {
            var xml = await _capture.CaptureAsync(context.Database, probe, context.CancellationToken);
            context.RecordServerVersion(ExtractBuild(xml));
            var columnConverts = ConvertImplicitDetector.FindColumnConversions(xml).Count > 0;
            var dynamicRangeSeek = xml.Contains("GetRangeThroughConvert", StringComparison.Ordinal);
            context.Entries.Add(new TypePairProbeResult(columnCategory, otherCategory, context.CollationName, columnConverts, CompileFailed: false, dynamicRangeSeek));
        }
        catch (SqlException ex) when (TypeIncompatibilityErrorNumbers.Contains(ex.Number))
        {
            context.Entries.Add(new TypePairProbeResult(columnCategory, otherCategory, context.CollationName, ColumnConverts: false, CompileFailed: true, DynamicRangeSeekAvailable: false));
        }
    }

    /// <summary>Bundles a probe run's fixed context (S107: keeps ProbeFamilyAsync/ProbeOnePairAsync's own parameter lists to just what varies per call).</summary>
    private sealed record ProbeContext(
        string Database, string? CollationName, System.Collections.Concurrent.ConcurrentBag<TypePairProbeResult> Entries,
        Action<string> RecordServerVersion, CancellationToken CancellationToken);

    private static string SanitizeForIdentifier(string collation)
    {
        Span<char> buffer = stackalloc char[collation.Length];
        for (var i = 0; i < collation.Length; i++)
        {
            buffer[i] = char.IsLetterOrDigit(collation[i]) ? collation[i] : '_';
        }

        return new string(buffer);
    }

    private static string ExtractBuild(string planXml)
    {
        const string marker = "Build=\"";
        var index = planXml.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return "unknown";
        }

        var start = index + marker.Length;
        var end = planXml.IndexOf('"', start);
        return end < 0 ? "unknown" : planXml[start..end];
    }
}

/// <summary>One probed cell, before being written to the checked-in JSON (see SilentScan.Core.Rules.TypePairOutcome for the consumed shape).</summary>
public sealed record TypePairProbeResult(
    SqlTypeCategory ColumnCategory,
    SqlTypeCategory OtherCategory,
    string? CollationName,
    bool ColumnConverts,
    bool CompileFailed,
    bool DynamicRangeSeekAvailable);
