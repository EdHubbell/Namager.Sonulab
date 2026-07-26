using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sonulab.Core.Model;

internal class SnapshotSlotKindConverter : JsonStringEnumConverter<SnapshotSlotKind>
{
    public SnapshotSlotKindConverter() : base(JsonNamingPolicy.CamelCase)
    {
    }
}

[JsonConverter(typeof(SnapshotSlotKindConverter))]
public enum SnapshotSlotKind { Preset, Amp, Ir }

/// <summary>Tone3000 identity for a slot. Populated for IRs resolved through the local index;
/// null otherwise, including for every amp until amps carry machine-readable ids.</summary>
public sealed record SnapshotT3k(
    [property: JsonPropertyName("toneId")] long ToneId,
    [property: JsonPropertyName("modelId")] long ModelId);

public sealed record SnapshotSlot(
    [property: JsonPropertyName("kind")] SnapshotSlotKind Kind,
    [property: JsonPropertyName("idx")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("t3k")] SnapshotT3k? T3k);

public sealed record SnapshotDevice(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("fw")] string Fw);

/// <summary>manifest.json inside a .namsnap. Names ARE recorded here — this is the user's own
/// backup of their own pedal. It is the telemetry path that never sees names, not this file.</summary>
public sealed record SnapshotManifest(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("device")] SnapshotDevice Device,
    [property: JsonPropertyName("slots")] IReadOnlyList<SnapshotSlot> Slots)
{
    public const int CurrentSchema = 1;
}
