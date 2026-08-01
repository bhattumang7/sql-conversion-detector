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
    /// both directions (string column vs numeric/datetime/guid value, and vice versa) - the
    /// classic "varchar column vs int/date parameter" bug class the blanket
    /// precedence-list-only heuristic used to guess at instead of probing.
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
    /// Probes <paramref name="numericFamily"/> and <paramref name="dateTimeFamily"/> (no
    /// collation dimension) plus <paramref name="stringFamily"/> under every entry of
    /// <paramref name="collations"/>, and returns the combined outcome list plus the server
    /// build string extracted from the first successfully-captured plan.
    /// </summary>
    public async Task<(IReadOnlyList<TypePairProbeResult> Entries, string ServerVersion)> GenerateAsync(
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> numericFamily,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> dateTimeFamily,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> stringFamily,
        IReadOnlyList<string> collations,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)>? crossFamilyOther = null,
        CancellationToken cancellationToken = default)
    {
        crossFamilyOther ??= [];
        var entries = new List<TypePairProbeResult>();
        string? serverVersion = null;

        if (numericFamily.Count > 0 || dateTimeFamily.Count > 0)
        {
            const string familyDb = "SilentScanTypeMatrixFamily";
            SqlConnection.ClearAllPools();
            await _provisioner.CreateFreshAsync(familyDb, cancellationToken: cancellationToken);
            try
            {
                await DeployFamilyTablesAsync(familyDb, numericFamily.Concat(dateTimeFamily).ToList(), collationName: null, cancellationToken);
                await ProbeFamilyAsync(familyDb, numericFamily, collationName: null, entries, v => serverVersion ??= v, cancellationToken);
                await ProbeFamilyAsync(familyDb, dateTimeFamily, collationName: null, entries, v => serverVersion ??= v, cancellationToken);

                // Non-string column vs a string-typed value (e.g. `WHERE IntColumn = '123'`) is
                // not collation-sensitive - the target isn't a string, so which collation family
                // the string literal notionally belongs to has no bearing on whether the column
                // converts. Probe this direction exactly once (default collation, recorded as
                // CollationName=null to match how VerdictClassifier looks up non-string columns)
                // rather than once per collation family, which would both waste probes and
                // collide on the matrix's (Column, Other, Collation) dictionary key.
                if (crossFamilyOther.Count > 0 && stringFamily.Count > 0)
                {
                    await DeployFamilyTablesAsync(familyDb, stringFamily, collationName: null, cancellationToken);
                    foreach (var (otherCategory, _) in crossFamilyOther)
                    {
                        foreach (var (stringCategory, stringSyntax) in stringFamily)
                        {
                            await ProbeOnePairAsync(familyDb, otherCategory, stringCategory, stringSyntax, collationName: null, entries, v => serverVersion ??= v, cancellationToken);
                        }
                    }
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

            var stringDb = "SilentScanTypeMatrixStr_" + SanitizeForIdentifier(collation);
            await _provisioner.CreateFreshAsync(stringDb, collationName: collation, cancellationToken: cancellationToken);
            try
            {
                await DeployFamilyTablesAsync(stringDb, stringFamily, collation, cancellationToken);
                await ProbeFamilyAsync(stringDb, stringFamily, collation, entries, v => serverVersion ??= v, cancellationToken);

                // String column vs a non-string value: collation-sensitive because it decides
                // whether a resulting dynamic seek is available, so this direction IS probed
                // once per collation family.
                if (crossFamilyOther.Count > 0)
                {
                    await DeployFamilyTablesAsync(stringDb, crossFamilyOther, collationName: null, cancellationToken);
                    foreach (var (stringCategory, _) in stringFamily)
                    {
                        foreach (var (otherCategory, otherSyntax) in crossFamilyOther)
                        {
                            await ProbeOnePairAsync(stringDb, stringCategory, otherCategory, otherSyntax, collation, entries, v => serverVersion ??= v, cancellationToken);
                        }
                    }
                }
            }
            finally
            {
                await _provisioner.DropIfExistsAsync(stringDb, cancellationToken);
                SqlConnection.ClearAllPools();
            }
        }

        return (entries, serverVersion ?? "unknown");
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

    private async Task ProbeFamilyAsync(
        string database,
        IReadOnlyList<(SqlTypeCategory Category, string Syntax)> family,
        string? collationName,
        List<TypePairProbeResult> entries,
        Action<string> recordServerVersion,
        CancellationToken cancellationToken)
    {
        foreach (var (columnCategory, _) in family)
        {
            foreach (var (otherCategory, otherSyntax) in family)
            {
                if (columnCategory == otherCategory)
                {
                    continue;
                }

                await ProbeOnePairAsync(database, columnCategory, otherCategory, otherSyntax, collationName, entries, recordServerVersion, cancellationToken);
            }
        }
    }

    private async Task ProbeOnePairAsync(
        string database,
        SqlTypeCategory columnCategory,
        SqlTypeCategory otherCategory,
        string otherSyntax,
        string? collationName,
        List<TypePairProbeResult> entries,
        Action<string> recordServerVersion,
        CancellationToken cancellationToken)
    {
        var probe = $"DECLARE @p {otherSyntax}; SELECT Col FROM dbo.T_{columnCategory} WHERE Col = @p;";
        try
        {
            var xml = await _capture.CaptureAsync(database, probe, cancellationToken);
            recordServerVersion(ExtractBuild(xml));
            var columnConverts = ConvertImplicitDetector.FindColumnConversions(xml).Count > 0;
            var dynamicRangeSeek = xml.Contains("GetRangeThroughConvert", StringComparison.Ordinal);
            entries.Add(new TypePairProbeResult(columnCategory, otherCategory, collationName, columnConverts, CompileFailed: false, dynamicRangeSeek));
        }
        catch (SqlException)
        {
            entries.Add(new TypePairProbeResult(columnCategory, otherCategory, collationName, ColumnConverts: false, CompileFailed: true, DynamicRangeSeekAvailable: false));
        }
    }

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
