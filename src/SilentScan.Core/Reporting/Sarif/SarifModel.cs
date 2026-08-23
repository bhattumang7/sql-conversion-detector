using System.Text.Json.Serialization;

namespace SilentScan.Core.Reporting.Sarif;

public sealed record SarifLog(
    [property: JsonPropertyName("$schema")] string Schema,
    string Version,
    IReadOnlyList<SarifRun> Runs);

public sealed record SarifRun(
    SarifTool Tool, IReadOnlyList<SarifResult> Results, IReadOnlyList<SarifInvocation>? Invocations = null);

public sealed record SarifInvocation(bool ExecutionSuccessful, IReadOnlyList<SarifNotification> ToolExecutionNotifications);

public sealed record SarifNotification(SarifMessage Message, string Level, IReadOnlyList<SarifLocation>? Locations = null);

public sealed record SarifTool(SarifDriver Driver);

public sealed record SarifDriver(string Name, string Version, string? InformationUri, IReadOnlyList<SarifRule> Rules);

public sealed record SarifRule(string Id, SarifMessage ShortDescription, string? HelpUri = null);

public sealed record SarifResult(
    string RuleId, string Level, SarifMessage Message, IReadOnlyList<SarifLocation> Locations, SarifResultProperties? Properties = null);

public sealed record SarifResultProperties(string Tier);

public sealed record SarifMessage(string Text);

public sealed record SarifLocation(SarifPhysicalLocation PhysicalLocation);

public sealed record SarifPhysicalLocation(SarifArtifactLocation ArtifactLocation, SarifRegion Region);

public sealed record SarifArtifactLocation(string Uri);

public sealed record SarifRegion(int StartLine, int? StartColumn);
