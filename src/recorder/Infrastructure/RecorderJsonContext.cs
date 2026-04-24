using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;

namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// Source-generated JSON serializer context for the recorder consumer. The
/// serializable set is rewritten to the BIFROST event surface — each event
/// type the dispatcher unwraps from <c>envelope.Payload</c>. Arena's Trader-*
/// DTOs are replaced by the split event shapes from the contracts-internal
/// fork.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Envelope<JsonElement>))]
[JsonSerializable(typeof(OrderAcceptedEvent))]
[JsonSerializable(typeof(OrderRejectedEvent))]
[JsonSerializable(typeof(OrderCancelledEvent))]
[JsonSerializable(typeof(OrderExecutedEvent))]
[JsonSerializable(typeof(MarketOrderRemainderCancelledEvent))]
[JsonSerializable(typeof(BookDeltaEvent))]
[JsonSerializable(typeof(PublicTradeEvent))]
[JsonSerializable(typeof(ImbalanceSettlementEvent))]
internal partial class RecorderJsonContext : JsonSerializerContext;
