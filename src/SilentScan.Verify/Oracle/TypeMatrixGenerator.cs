using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Deployment;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public sealed class TypeMatrixGenerator
{
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

public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> DateTimeFamily =
    [
        (SqlTypeCategory.Time, "TIME"),
        (SqlTypeCategory.Date, "DATE"),
        (SqlTypeCategory.SmallDateTime, "SMALLDATETIME"),
        (SqlTypeCategory.DateTime, "DATETIME"),
        (SqlTypeCategory.DateTime2, "DATETIME2"),
        (SqlTypeCategory.DateTimeOffset, "DATETIMEOFFSET"),
    ];

public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> BinaryFamily =
    [
        (SqlTypeCategory.Binary, "BINARY(16)"),
        (SqlTypeCategory.VarBinary, "VARBINARY(16)"),
        (SqlTypeCategory.Timestamp, "ROWVERSION"),
    ];

public static readonly IReadOnlyList<(SqlTypeCategory Category, string Syntax)> StringFamily =
    [
        (SqlTypeCategory.Char, "CHAR(40)"),
        (SqlTypeCategory.VarChar, "VARCHAR(40)"),
        (SqlTypeCategory.NChar, "NCHAR(40)"),
        (SqlTypeCategory.NVarChar, "NVARCHAR(40)"),
    ];

public static readonly IReadOnlyList<string> Collations =
    [
        "SQL_Latin1_General_CP1_CI_AS",
        "Latin1_General_CI_AS",
        "Latin1_General_100_CI_AS_SC_UTF8",
        "Latin1_General_BIN2",
    ];

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
        var entries = new System.Collections.Concurrent.ConcurrentBag<TypePairProbeResult>();
        string? serverVersion = null;

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

                await ProbeFamilyAsync(baseFamily, familyContext);

                if (needsCrossFamilyProbing)
                {
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

private const int MaxConcurrentProbes = 4;

    private Task ProbeFamilyAsync(IReadOnlyList<(SqlTypeCategory Category, string Syntax)> family, ProbeContext context) =>
        ProbePairsAsync(
            family
                .SelectMany(column => family.Select(other => (column.Category, other.Category, other.Syntax)))
                .Where(p => p.Item1 != p.Item2),
            context);

private async Task ProbePairsAsync(
        IEnumerable<(SqlTypeCategory ColumnCategory, SqlTypeCategory OtherCategory, string OtherSyntax)> pairs, ProbeContext context)
    {
        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentProbes, CancellationToken = context.CancellationToken },
            (pair, ct) => new ValueTask(ProbeOnePairAsync(pair.ColumnCategory, pair.OtherCategory, pair.OtherSyntax, context with { CancellationToken = ct })));
    }

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

public sealed record TypePairProbeResult(
    SqlTypeCategory ColumnCategory,
    SqlTypeCategory OtherCategory,
    string? CollationName,
    bool ColumnConverts,
    bool CompileFailed,
    bool DynamicRangeSeekAvailable);
