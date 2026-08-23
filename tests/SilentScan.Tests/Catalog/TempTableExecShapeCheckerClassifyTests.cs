using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Live.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Catalog;

public sealed class TempTableExecShapeCheckerClassifyTests
{
    private static readonly TempTableExecShapeCandidate Candidate = new(
        TempTableQualifiedName: "#Results",
        TempTableColumns: null,
        ExecutedProcQualifiedName: "dbo.usp_Callee",
        CallerScopeQualifiedName: "dbo.usp_Caller",
        SourcePath: "dbo.usp_Caller",
        Line: 4,
        Column: 5);

    private static DescribedResultColumn Column(string typeName, short maxLength = 0, byte precision = 0, byte scale = 0) =>
        new("Col", new LiveLineageParityChecker.ActualColumn(typeName, maxLength, precision, scale, CollationName: null));

    [Fact]
    public void MatchingCountAndCompatibleTypes_NoFindings()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("int") };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        Assert.Empty(findings);
    }

    [Fact]
    public void FewerDescribedColumnsThanDeclared_ColumnCountMismatch()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
            new("Name", new SqlType(SqlTypeCategory.VarChar, Length: 50), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("int") };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        var finding = Assert.Single(findings);
        Assert.Equal(TempTableExecShapeFindingKind.ColumnCountMismatch, finding.Kind);
        Assert.Equal(2, finding.TempTableDeclaredColumnCount);
        Assert.Equal(1, finding.DescribedColumnCount);
        Assert.Null(finding.ColumnPosition);
        Assert.Null(finding.WriteLoss);
    }

    [Fact]
    public void MoreDescribedColumnsThanDeclared_ColumnCountMismatch()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("int"), Column("varchar", maxLength: 50) };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        var finding = Assert.Single(findings);
        Assert.Equal(TempTableExecShapeFindingKind.ColumnCountMismatch, finding.Kind);
    }

    [Fact]
    public void MatchingCountUnicodeDescribedIntoNonUnicodeDeclared_ColumnTypeMismatch()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Name", new SqlType(SqlTypeCategory.VarChar, Length: 50), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("nvarchar", maxLength: 100) };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        var finding = Assert.Single(findings);
        Assert.Equal(TempTableExecShapeFindingKind.ColumnTypeMismatch, finding.Kind);
        Assert.Equal(1, finding.ColumnPosition);
        Assert.Equal("Name", finding.ColumnName);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.WriteLoss);
    }

    [Fact]
    public void MatchingCountFloatDescribedIntoIntDeclared_ColumnTypeMismatch()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Total", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("float") };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.ApproximateToExactTruncation, finding.WriteLoss);
    }

    [Fact]
    public void OnlyTheMismatchedPositionIsReported_MatchingPositionsStaySilent()
    {
        var tempColumns = new List<CatalogColumn>
        {
            new("Id", new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
            new("Name", new SqlType(SqlTypeCategory.VarChar, Length: 50), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false),
        };
        var described = new List<DescribedResultColumn> { Column("int"), Column("nvarchar", maxLength: 100) };

        var findings = new List<TempTableExecShapeFinding>();
        TempTableExecShapeChecker.Classify(Candidate, tempColumns, described, findings);

        var finding = Assert.Single(findings);
        Assert.Equal(2, finding.ColumnPosition);
        Assert.Equal("Name", finding.ColumnName);
    }
}
