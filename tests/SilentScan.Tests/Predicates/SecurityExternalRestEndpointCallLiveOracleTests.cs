using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class SecurityExternalRestEndpointCallLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_CallToExternalRestEndpoint_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            EXEC sp_invoke_external_rest_endpoint @url = 'https://example.com/webhook';
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<SecurityFinding>("SecurityScanner"), f => f.Kind == SecurityFindingKind.ExternalRestEndpointCall);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task LiveDeployment_CallToUnrelatedProcedure_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            EXEC sp_who;
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.DoesNotContain(report.Find<SecurityFinding>("SecurityScanner"), f => f.Kind == SecurityFindingKind.ExternalRestEndpointCall);
    }
}
