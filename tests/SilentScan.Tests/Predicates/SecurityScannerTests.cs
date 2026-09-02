using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SecurityScannerTests
{
    private static IReadOnlyList<SecurityFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return SecurityScanner.Scan(result);
    }

    [Fact]
    public void DeclareWithCredentialNameAndLiteral_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @Password VARCHAR(50) = 'hunter2';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void SetCredentialNameToLiteral_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @Passwd VARCHAR(50);
                SET @Passwd = 'literal';
            END
            """);

        Assert.Contains(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void SelectAssignCredentialNameToLiteral_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @Secret VARCHAR(50);
                SELECT @Secret = 'literal';
            END
            """);

        Assert.Contains(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void CredentialNameAssignedFromVariable_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @Password VARCHAR(50);
                DECLARE @Input VARCHAR(50);
                SET @Password = @Input;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void VariableNameContainingPwdAsSubstringNotWholeWord_NeverFires()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @VehInOpWD VARCHAR(50);
                SET @VehInOpWD = 'literal';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void VariableNameWithPwdAsWholeWord_NeverFires()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @GetPWDTrips VARCHAR(50);
                SET @GetPWDTrips = 'literal';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void NonCredentialNameAssignedLiteral_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @Name VARCHAR(50) = 'John';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedCredential);
    }

    [Fact]
    public void LiteralWithRealIpAddress_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT 'Server=10.20.30.40;Port=1433' AS ConnStr;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == SecurityFindingKind.HardCodedIpAddress);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("10.20.30.40", finding.DetailText);
    }

    [Fact]
    public void LiteralWithLoopbackAddress_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT '127.0.0.1' AS Loopback;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedIpAddress);
    }

    [Fact]
    public void LiteralWithDocumentationTestNetAddress_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT '192.0.2.5' AS ExampleIp;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedIpAddress);
    }

    [Fact]
    public void LiteralWithOctetOver255_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT '999.1.1.1' AS NotAnIp;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedIpAddress);
    }

    [Fact]
    public void PlainStringLiteral_NeverFiresIpAddress()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT 'hello world' AS Greeting;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.HardCodedIpAddress);
    }

    [Fact]
    public void CallToExternalRestEndpoint_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                EXEC sp_invoke_external_rest_endpoint @url = 'https://example.com/webhook';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == SecurityFindingKind.ExternalRestEndpointCall);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void CallToUnrelatedProcedure_NeverFiresExternalRestEndpoint()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                EXEC dbo.SomeOtherProcedure @Value = 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == SecurityFindingKind.ExternalRestEndpointCall);
    }

    [Fact]
    public void HashBytesMd5GeneralUse_FiresGeneralKind()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT HASHBYTES('MD5', Payload) AS Checksum FROM dbo.T;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind is SecurityFindingKind.WeakHashAlgorithm or SecurityFindingKind.WeakHashAlgorithmInSensitiveContext);
        Assert.Equal(SecurityFindingKind.WeakHashAlgorithm, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void HashBytesSha1OnCredentialColumn_FiresSensitiveKind()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT HASHBYTES('SHA1', Password) AS Hashed FROM dbo.T;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind is SecurityFindingKind.WeakHashAlgorithm or SecurityFindingKind.WeakHashAlgorithmInSensitiveContext);
        Assert.Equal(SecurityFindingKind.WeakHashAlgorithmInSensitiveContext, finding.Kind);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void HashBytesMd5InComparisonPredicate_FiresSensitiveKind()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT * FROM dbo.T WHERE HASHBYTES('MD5', Val) = @Expected;
            END
            """);

        Assert.Contains(findings, f => f.Kind == SecurityFindingKind.WeakHashAlgorithmInSensitiveContext);
    }

    [Fact]
    public void HashBytesSha2_256_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT HASHBYTES('SHA2_256', Payload) AS Checksum FROM dbo.T;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind is SecurityFindingKind.WeakHashAlgorithm or SecurityFindingKind.WeakHashAlgorithmInSensitiveContext);
    }

    [Fact]
    public void UnanalyzableDynamicSqlFinding_MapsToUnprovableDynamicSqlText()
    {
        var input = new[]
        {
            new DynamicSqlFinding("test.sql", 5, 1, DynamicSqlOutcome.Unanalyzable, "depends on a parameter"),
            new DynamicSqlFinding("test.sql", 10, 1, DynamicSqlOutcome.AnalyzedLiteral, null),
        };

        var findings = SecurityScanner.FromDynamicSqlFindings(input);

        var finding = Assert.Single(findings);
        Assert.Equal(SecurityFindingKind.UnprovableDynamicSqlText, finding.Kind);
        Assert.Equal(5, finding.Line);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void DuplicateUnanalyzableFindingsAtSameSite_CollapseToOne()
    {
        var input = new[]
        {
            new DynamicSqlFinding("test.sql", 5, 1, DynamicSqlOutcome.Unanalyzable, "round 1"),
            new DynamicSqlFinding("test.sql", 5, 1, DynamicSqlOutcome.Unanalyzable, "round 2"),
            new DynamicSqlFinding("test.sql", 5, 1, DynamicSqlOutcome.Unanalyzable, "round 3"),
        };

        var findings = SecurityScanner.FromDynamicSqlFindings(input);

        Assert.Single(findings);
    }

    [Fact]
    public void NoUnanalyzableDynamicSqlFindings_ProducesNothing()
    {
        var input = new[]
        {
            new DynamicSqlFinding("test.sql", 5, 1, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("test.sql", 6, 1, DynamicSqlOutcome.InnerParseFailed, "bad parse"),
        };

        Assert.Empty(SecurityScanner.FromDynamicSqlFindings(input));
    }
}
