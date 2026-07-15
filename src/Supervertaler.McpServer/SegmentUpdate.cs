using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Supervertaler.McpServer;

/// <summary>One item of an update_segments call. Mirrors the bridge's BridgeSegmentUpdate.</summary>
public sealed class SegmentUpdate
{
    [JsonPropertyName("id"),
     Description("Segment id as returned by get_segments (\"<paragraphUnitId>:<segmentId>\").")]
    public string Id { get; set; } = "";

    [JsonPropertyName("target"),
     Description("New target text. Omit to change only the status. Preserve inline tag markers from the source.")]
    public string? Target { get; set; }

    [JsonPropertyName("status"),
     Description("Confirmation status to set: Unspecified, Draft, Translated, RejectedTranslation, " +
                 "ApprovedTranslation, RejectedSignOff or ApprovedSignOff. Omitted with a target write = Draft.")]
    public string? Status { get; set; }
}
