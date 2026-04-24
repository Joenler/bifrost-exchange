using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;

namespace Bifrost.Imbalance;

/// <summary>
/// Source-generated JSON context for the simulator's wire surface. The
/// fill consumer deserializes <see cref="Envelope{T}"/> with a
/// <see cref="JsonElement"/> payload first (mirrors the recorder's pattern)
/// so the envelope's MessageType can drive per-event payload materialization
/// without tying the generic Envelope{T} shape to a specific T.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Envelope<JsonElement>))]
[JsonSerializable(typeof(Envelope<OrderExecutedEvent>))]
[JsonSerializable(typeof(OrderExecutedEvent))]
[JsonSerializable(typeof(PhysicalShockEvent))]
[JsonSerializable(typeof(Envelope<PhysicalShockEvent>))]
[JsonSerializable(typeof(InstrumentIdDto))]
internal partial class ImbalanceJsonContext : JsonSerializerContext;
