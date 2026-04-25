using Bifrost.Contracts.Internal;
using Bifrost.Gateway.Translation;
using Xunit;
using EventsProto = Bifrost.Contracts.Events;
using MarketProto = Bifrost.Contracts.Market;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Translation;

/// <summary>
/// Production OutboundTranslator round-trip tests. Each [Fact] wraps a seeded DTO
/// in an Envelope, calls the production translator, and asserts the resulting
/// proto's oneof body is byte-equivalent to the canonical fixture proto.
///
/// Google.Protobuf messages have generated structural <c>Equals</c> at the
/// reference-implementation runtime; comparing the `body` proto (e.g.
/// <c>actual.OrderAck</c>) against the expected proto from the fixture mirror
/// is the equivalent of the byte-equivalence check the CONT-07 suite performs
/// using <c>ToByteArray()</c>.
/// </summary>
public sealed class OutboundTranslatorTests
{
    [Fact]
    public void FromAccepted_OrderAckByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.OrderAcceptedDto();
        var expectedBody = TranslationFixturesMirror.OrderAcceptedProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.OrderAccepted, dto);

        var actual = OutboundTranslator.FromAccepted(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderAck, actual.EventCase);
        Assert.Equal(expectedBody, actual.OrderAck);
        Assert.Equal(envelope.Sequence!.Value, actual.Sequence);
    }

    [Fact]
    public void FromRejected_OrderRejectByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.OrderRejectedDto();
        var expectedBody = TranslationFixturesMirror.OrderRejectedProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.OrderRejected, dto);

        var actual = OutboundTranslator.FromRejected(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, actual.EventCase);
        Assert.Equal(expectedBody, actual.OrderReject);
    }

    [Fact]
    public void FromExecuted_FillByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.OrderExecutedDto();
        var expectedBody = TranslationFixturesMirror.OrderExecutedProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.OrderExecuted, dto);

        var actual = OutboundTranslator.FromExecuted(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.Fill, actual.EventCase);
        Assert.Equal(expectedBody, actual.Fill);
    }

    [Fact]
    public void FromCancelled_OrderAckByteEquivalent()
    {
        var dto = TranslationFixturesMirror.OrderCancelledDto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.OrderCancelled, dto);

        var actual = OutboundTranslator.FromCancelled(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderAck, actual.EventCase);
        Assert.Equal(dto.OrderId, actual.OrderAck.OrderId);
        Assert.Equal(TranslationFixturesMirror.CanonicalInstrumentId, actual.OrderAck.Instrument.InstrumentId);
    }

    [Fact]
    public void FromBookDelta_BookUpdateByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.BookDeltaDto();
        var expectedBody = TranslationFixturesMirror.BookDeltaProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.BookDelta, dto);

        var actual = OutboundTranslator.FromBookDelta(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.BookUpdate, actual.EventCase);
        Assert.Equal(expectedBody, actual.BookUpdate);
    }

    [Fact]
    public void FromPublicTrade_TradeByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.PublicTradeDto();
        var expectedBody = TranslationFixturesMirror.PublicTradeProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.PublicTrade, dto);

        var actual = OutboundTranslator.FromPublicTrade(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.Trade, actual.EventCase);
        Assert.Equal(expectedBody, actual.Trade);
    }

    [Fact]
    public void FromForecastUpdate_ByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.ForecastUpdateDto();
        var expectedBody = TranslationFixturesMirror.ForecastUpdateProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.ForecastUpdate, dto);

        var actual = OutboundTranslator.FromForecastUpdate(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.ForecastUpdate, actual.EventCase);
        Assert.Equal(expectedBody, actual.ForecastUpdate);
    }

    [Fact]
    public void FromForecastRevision_PublicEventByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.ForecastRevisionDto();
        var expectedBody = TranslationFixturesMirror.ForecastRevisionProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.ForecastRevision, dto);

        var actual = OutboundTranslator.FromForecastRevision(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PublicEvent, actual.EventCase);
        Assert.Equal(EventsProto.Event.PayloadOneofCase.ForecastRevision, actual.PublicEvent.PayloadCase);
        Assert.Equal(expectedBody, actual.PublicEvent.ForecastRevision);
    }

    [Fact]
    public void FromRegimeChange_PublicEventByteEquivalentToFixture()
    {
        var jsonElement = TranslationFixturesMirror.RegimeChangeJson();
        var expectedBody = TranslationFixturesMirror.RegimeChangeProto();
        var envelope = TranslationFixturesMirror.WrapEnvelopeRaw(MessageTypes.RegimeChange, jsonElement);

        var actual = OutboundTranslator.FromRegimeChange(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PublicEvent, actual.EventCase);
        Assert.Equal(EventsProto.Event.PayloadOneofCase.RegimeChange, actual.PublicEvent.PayloadCase);
        Assert.Equal(expectedBody, actual.PublicEvent.RegimeChange);
    }

    [Fact]
    public void FromPhysicalShock_PublicEventByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.PhysicalShockDto();
        var expectedBody = TranslationFixturesMirror.PhysicalShockProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.PhysicalShock, dto);

        var actual = OutboundTranslator.FromPhysicalShock(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PublicEvent, actual.EventCase);
        Assert.Equal(EventsProto.Event.PayloadOneofCase.PhysicalShock, actual.PublicEvent.PayloadCase);
        Assert.Equal(expectedBody, actual.PublicEvent.PhysicalShock);
    }

    [Fact]
    public void FromImbalancePrint_ByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.ImbalancePrintDto();
        var expectedBody = TranslationFixturesMirror.ImbalancePrintProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.ImbalancePrint, dto);

        var actual = OutboundTranslator.FromImbalancePrint(envelope, TranslationFixturesMirror.FullContext());

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.ImbalancePrint, actual.EventCase);
        Assert.Equal(expectedBody, actual.ImbalancePrint);
    }

    [Fact]
    public void FromImbalanceSettlement_NoOneofSet()
    {
        var dto = TranslationFixturesMirror.ImbalanceSettlementDto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.ImbalanceSettlement, dto);

        var actual = OutboundTranslator.FromImbalanceSettlement(envelope);

        // Per gateway-mapping.md: ImbalanceSettlement is private; no gRPC analog.
        // Translator validates the payload but produces an envelope-only MarketEvent.
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.None, actual.EventCase);
        Assert.Equal(envelope.Sequence!.Value, actual.Sequence);
    }

    [Fact]
    public void FromRoundState_RoundStateByteEquivalentToFixture()
    {
        var dto = TranslationFixturesMirror.RoundStateDto();
        var expectedBody = TranslationFixturesMirror.RoundStateProto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.RoundStateChanged, dto);

        var actual = OutboundTranslator.FromRoundState(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RoundState, actual.EventCase);
        Assert.Equal(expectedBody, actual.RoundState);
    }

    [Fact]
    public void FromAuctionClearingResult_PublicEventNewsCarriesJson()
    {
        var dto = TranslationFixturesMirror.AuctionClearingResultDto();
        var envelope = TranslationFixturesMirror.WrapEnvelope(MessageTypes.AuctionClearingResult, dto);

        var actual = OutboundTranslator.FromAuctionClearingResult(envelope);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PublicEvent, actual.EventCase);
        Assert.Equal(EventsProto.Event.PayloadOneofCase.News, actual.PublicEvent.PayloadCase);
        Assert.Equal("auction.clearing", actual.PublicEvent.News.LibraryKey);
        // News.Text is the JSON-serialized clearing row; teams parse it client-side.
        Assert.Contains("\"quarterId\":\"Q1\"", actual.PublicEvent.News.Text);
        Assert.Contains("\"teamName\":\"alpha\"", actual.PublicEvent.News.Text);
    }

    [Fact]
    public void BuildRegisterAck_PopulatesEveryField()
    {
        var roundState = TranslationFixturesMirror.RoundStateProto();
        var ack = OutboundTranslator.BuildRegisterAck(
            clientId: TranslationFixturesMirror.CanonicalClientId,
            currentRoundState: roundState,
            resumedFromSequence: 42L,
            reregisterRequired: false,
            sequence: 1L,
            timestampNs: TranslationFixturesMirror.CanonicalTimestampNs);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RegisterAck, ack.EventCase);
        Assert.Equal(TranslationFixturesMirror.CanonicalClientId, ack.RegisterAck.ClientId);
        Assert.Equal(42L, ack.RegisterAck.ResumedFromSequence);
        Assert.False(ack.RegisterAck.ReregisterRequired);
        Assert.Equal(roundState, ack.RegisterAck.CurrentRoundState);
        Assert.Equal(1L, ack.Sequence);
        Assert.Equal(TranslationFixturesMirror.CanonicalTimestampNs, ack.TimestampNs);
    }

    [Fact]
    public void BuildRegisterAck_ReregisterRequiredTrue_PreservesFlag()
    {
        var ack = OutboundTranslator.BuildRegisterAck(
            clientId: TranslationFixturesMirror.CanonicalClientId,
            currentRoundState: new RoundProto.RoundState(),
            resumedFromSequence: 0L,
            reregisterRequired: true);

        Assert.True(ack.RegisterAck.ReregisterRequired);
        Assert.Equal(0L, ack.RegisterAck.ResumedFromSequence);
    }

    [Fact]
    public void BuildPositionSnapshot_PopulatesEveryField()
    {
        var instrumentDto = TranslationFixturesMirror.InstrumentDto();
        var snap = OutboundTranslator.BuildPositionSnapshot(
            instrumentId: instrumentDto,
            instrumentIdString: TranslationFixturesMirror.CanonicalInstrumentId,
            productType: TranslationFixturesMirror.CanonicalProductType,
            netPositionTicks: 5L,
            averagePriceTicks: 100L,
            openOrdersNotionalTicks: 250L,
            sequence: 3L,
            timestampNs: TranslationFixturesMirror.CanonicalTimestampNs);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot, snap.EventCase);
        Assert.Equal(TranslationFixturesMirror.CanonicalInstrumentId, snap.PositionSnapshot.Instrument.InstrumentId);
        Assert.Equal(MarketProto.ProductType.Hour, snap.PositionSnapshot.Instrument.ProductType);
        Assert.Equal(5L, snap.PositionSnapshot.NetPositionTicks);
        Assert.Equal(100L, snap.PositionSnapshot.AveragePriceTicks);
        Assert.Equal(250L, snap.PositionSnapshot.OpenOrdersNotionalTicks);
        Assert.Equal(3L, snap.Sequence);
    }

    [Fact]
    public void BuildOrderReject_PopulatesEveryField()
    {
        var reject = OutboundTranslator.BuildOrderReject(
            reason: StrategyProto.RejectReason.Structural,
            detail: "reserved client_id",
            clientOrderId: "co-99",
            sequence: 7L,
            timestampNs: TranslationFixturesMirror.CanonicalTimestampNs);

        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, reject.EventCase);
        Assert.Equal(StrategyProto.RejectReason.Structural, reject.OrderReject.Reason);
        Assert.Equal("reserved client_id", reject.OrderReject.Detail);
        Assert.Equal("co-99", reject.OrderReject.ClientOrderId);
        Assert.Equal(7L, reject.Sequence);
    }

    [Fact]
    public void BuildOrderReject_NullClientOrderId_DefaultsToEmpty()
    {
        var reject = OutboundTranslator.BuildOrderReject(
            reason: StrategyProto.RejectReason.RateLimited,
            detail: "1s timeout",
            clientOrderId: null);

        Assert.Equal(string.Empty, reject.OrderReject.ClientOrderId);
    }

    [Fact]
    public void RejectReasonMap_RoundTripsAllValues()
    {
        var allValues = Enum.GetValues<StrategyProto.RejectReason>()
            .Where(r => r != StrategyProto.RejectReason.Unspecified)
            .ToArray();

        foreach (var r in allValues)
        {
            var s = RejectReasonMap.EnumToString(r);
            var rt = RejectReasonMap.StringToEnum(s);
            Assert.Equal(r, rt);
        }
        // Verify count is at least 12 (the SPEC-defined surface) — this guards
        // against silent enum truncation.
        Assert.True(allValues.Length >= 12, $"Expected >= 12 RejectReason values; saw {allValues.Length}.");
    }

    [Fact]
    public void RejectReasonMap_UnspecifiedEnum_Throws()
    {
        Assert.Throws<ArgumentException>(() => RejectReasonMap.EnumToString(StrategyProto.RejectReason.Unspecified));
    }

    [Fact]
    public void RejectReasonMap_UnknownString_Throws()
    {
        Assert.Throws<ArgumentException>(() => RejectReasonMap.StringToEnum("NotARealReason"));
    }
}
