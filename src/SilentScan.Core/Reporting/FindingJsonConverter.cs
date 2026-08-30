using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed class FindingJsonConverter : JsonConverter<IFinding>
{
    public override IFinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Findings are never deserialized.");

    public override void Write(Utf8JsonWriter writer, IFinding value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
