using System.Text.Json.Serialization;
using Bifrost.Contracts.Internal.Auction;

namespace Bifrost.DahAuction;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> covering the three
/// auction DTOs. Registered on <c>ConfigureHttpJsonOptions</c> in Program.cs
/// so request-body deserialization and response-body serialization skip the
/// reflection-based STJ fast-path.
/// </summary>
[JsonSerializable(typeof(BidMatrixDto))]
[JsonSerializable(typeof(BidStepDto))]
[JsonSerializable(typeof(ClearingResultDto))]
internal partial class AuctionJsonSerializerContext : JsonSerializerContext { }
