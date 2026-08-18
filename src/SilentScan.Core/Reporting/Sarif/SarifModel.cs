using System.Text.Json.Serialization;

namespace SilentScan.Core.Reporting.Sarif;

// Minimal SARIF 2.1.0 object model (https://docs.oasis-open.org/sarif/sarif/v2.1.0/) -
// only the shapes CLAUDE.md's "SARIF export so the tool doubles as a CI gate" needs.

public sealed record SarifLog(
    [property: JsonPropertyName("$schema")] string Schema,
    string Version,
    IReadOnlyList<SarifRun> Runs);

public sealed record SarifRun(SarifTool Tool, IReadOnlyList<SarifResult> Results);

public sealed record SarifTool(SarifDriver Driver);

public sealed record SarifDriver(string Name, string Version, string? InformationUri, IReadOnlyList<SarifRule> Rules);

public sealed record SarifRule(string Id, SarifMessage ShortDescription);

public sealed record SarifResult(
    string RuleId, string Level, SarifMessage Message, IReadOnlyList<SarifLocation> Locations, SarifResultProperties? Properties = null);

/// <summary>
/// SARIF's own <c>result.properties</c> extensibility bag, holding exactly one custom key:
/// <see cref="Tier"/> - the same <c>Proven</c>/<c>Contextual</c>/<c>Advisory</c> vocabulary a
/// surveyed incumbent tool uses (docs/detection-checklist.md "Reporting ideas worth stealing").
/// A named tier lets a CI consumer gate on stable semantics ("only fail on Proven") instead of
/// SARIF's own <c>level</c>, whose error/warning/note meaning is configured differently by every
/// consumer - <c>level</c> stays exactly as before for tools that already key off it.
/// </summary>
public sealed record SarifResultProperties(string Tier);

public sealed record SarifMessage(string Text);

public sealed record SarifLocation(SarifPhysicalLocation PhysicalLocation);

public sealed record SarifPhysicalLocation(SarifArtifactLocation ArtifactLocation, SarifRegion Region);

public sealed record SarifArtifactLocation(string Uri);

public sealed record SarifRegion(int StartLine, int? StartColumn);
