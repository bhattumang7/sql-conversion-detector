using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Regenerates the checked-in oracle-probed type-pair matrix consumed by
/// <c>SilentScan.Core.Rules.TypePairMatrix</c> (docs/audit-remediation-plan.md Phase 0.2). For
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

    /// <summary>The two collation families CLAUDE.md's type rules distinguish: legacy SQL_* (no dynamic range seek) and Windows (dynamic range seek available).</summary>
    public static readonly IReadOnlyList<string> Collations = ["SQL_Latin1_General_CP1_CI_AS", "Latin1_General_CI_AS"];

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
        CancellationToken cancellationToken = default)
    {
        var entries = new List<TypePairProbeResult>();
        string? serverVersion = null;

        if (numericFamily.Count > 0 || dateTimeFamily.Count > 0)
        {
            const string familyDb = "SilentScanTypeMatrixFamily";
            SqlConnection.ClearAllPools();
            await _provisioner.CreateFreshAsync(familyDb, cancellationToken);
            try
            {
                await DeployFamilyTablesAsync(familyDb, numericFamily.Concat(dateTimeFamily).ToList(), collationName: null, cancellationToken);
                await ProbeFamilyAsync(familyDb, numericFamily, collationName: null, entries, v => serverVersion ??= v, cancellationToken);
                await ProbeFamilyAsync(familyDb, dateTimeFamily, collationName: null, entries, v => serverVersion ??= v, cancellationToken);
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

            var stringDb = "SilentScanTypeMatrixStr_" + Math.Abs(collation.GetHashCode(StringComparison.Ordinal));
            await _provisioner.CreateFreshAsync(stringDb, cancellationToken);
            try
            {
                await DeployFamilyTablesAsync(stringDb, stringFamily, collation, cancellationToken);
                await ProbeFamilyAsync(stringDb, stringFamily, collation, entries, v => serverVersion ??= v, cancellationToken);
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
                    // The pair is not implicitly comparable at all (e.g. TIME vs DATE) - a real,
                    // worth-recording outcome, not a probe failure to propagate.
                    entries.Add(new TypePairProbeResult(columnCategory, otherCategory, collationName, ColumnConverts: false, CompileFailed: true, DynamicRangeSeekAvailable: false));
                }
            }
        }
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
