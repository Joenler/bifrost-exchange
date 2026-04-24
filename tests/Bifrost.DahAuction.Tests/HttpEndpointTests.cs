using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Tests.Fixtures;
using Bifrost.Exchange.Application;
using Xunit;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.DahAuction.Tests;

/// <summary>
/// HTTP-level assertions over the in-process Kestrel endpoint. Each test
/// builds its own <see cref="TestAuctionHost"/> (no state sharing) and POSTs
/// directly via the harness <see cref="HttpClient"/>. Coverage: status codes
/// (200, 400, 415), validator code vocabulary, Kestrel body-cap, content-type
/// enforcement, negative-price acceptance.
/// </summary>
public sealed class HttpEndpointTests
{
    private static string FirstQuarterId() =>
        TradingCalendar.GenerateInstruments()
            .First(i => (i.DeliveryPeriod.End - i.DeliveryPeriod.Start) == TimeSpan.FromMinutes(15))
            .ToString();

    private static BidMatrixDto ValidBid(string team = "alpha", string? quarter = null) =>
        new(team, quarter ?? FirstQuarterId(),
            new BidStepDto[] { new(100L, 10L) },
            Array.Empty<BidStepDto>());

    [Fact]
    public async Task BidAccepted_During_AuctionOpen_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", ValidBid(), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("accepted", body);
    }

    [Theory]
    [InlineData(RoundStateEnum.IterationOpen)]
    [InlineData(RoundStateEnum.AuctionClosed)]
    [InlineData(RoundStateEnum.RoundOpen)]
    [InlineData(RoundStateEnum.Gate)]
    [InlineData(RoundStateEnum.Settled)]
    [InlineData(RoundStateEnum.Aborted)]
    public async Task BidRejected_Outside_AuctionOpen_Returns400_WithAuctionNotOpen(RoundStateEnum state)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(state);
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", ValidBid(), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("AuctionNotOpen", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceTeamName_Returns400_UnknownTeam(string teamName)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", ValidBid(team: teamName), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("UnknownTeam", body);
    }

    [Fact]
    public async Task UnregisteredQuarter_Returns400_UnknownQuarter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var resp = await host.Client.PostAsJsonAsync(
            "/auction/bid", ValidBid(quarter: "DE.Quarter.NotRealQh"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("UnknownQuarter", body);
    }

    [Fact]
    public async Task NonMonotonicBuy_Returns400_NonMonotonic()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var bid = new BidMatrixDto(
            "alpha", FirstQuarterId(),
            // Ascending instead of strictly descending — invalid for buys.
            new BidStepDto[] { new(50L, 10L), new(100L, 10L) },
            Array.Empty<BidStepDto>());
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", bid, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("NonMonotonic", body);
    }

    [Fact]
    public async Task TooManyBuySteps_Returns400_TooManySteps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        // 21 strictly-descending steps > MaxStepsPerSide=20.
        var buys = Enumerable.Range(0, 21)
            .Select(i => new BidStepDto(1_000_000L - i * 1_000L, 10L))
            .ToArray();
        var bid = new BidMatrixDto("alpha", FirstQuarterId(), buys, Array.Empty<BidStepDto>());
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", bid, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("TooManySteps", body);
    }

    [Fact]
    public async Task ZeroQuantity_Returns400_NonPositiveQuantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var bid = new BidMatrixDto(
            "alpha", FirstQuarterId(),
            new BidStepDto[] { new(100L, 0L) },
            Array.Empty<BidStepDto>());
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", bid, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("NonPositiveQuantity", body);
    }

    [Fact]
    public async Task MalformedJson_Returns400_NotAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        // Truncated JSON — System.Text.Json fails to deserialize at the
        // model-binding layer before the validator runs. ASP.NET Core minimal
        // APIs return 400 for parameter binding failures; the response is
        // intentionally NOT 200.
        var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
        var resp = await host.Client.PostAsync("/auction/bid", content, ct);
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task WrongContentType_Returns415_UnsupportedMediaType()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        // Non-JSON content type — the .Accepts<BidMatrixDto>("application/json")
        // metadata makes Kestrel return 415 for non-JSON bodies.
        var content = new StringContent("some plain text", Encoding.UTF8, "text/plain");
        var resp = await host.Client.PostAsync("/auction/bid", content, ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task NegativePrice_Accepted_Returns200()
    {
        // Nordic / CE DAH convention: negative prices are valid bids.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);
        var bid = new BidMatrixDto(
            "alpha", FirstQuarterId(),
            new BidStepDto[] { new(-500L, 10L) },
            Array.Empty<BidStepDto>());
        var resp = await host.Client.PostAsJsonAsync("/auction/bid", bid, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_RejectedByKestrel()
    {
        // Body cap is configured at MaxRequestBodySize = 65536 bytes
        // (appsettings.json copied from src/dah-auction). A payload >64KB
        // must NOT result in a 200 — Kestrel may return 400 or 413.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestAuctionHost.StartAsync(RoundStateEnum.AuctionOpen);

        // Build a syntactically-valid but oversized JSON body. A long padded
        // team name pushes well past 64 KB without tripping the validator's
        // structural checks.
        var hugeName = new string('x', 80_000);
        var oversizedJson =
            "{\"teamName\":\"" + hugeName + "\","
            + "\"quarterId\":\"" + FirstQuarterId() + "\","
            + "\"buySteps\":[{\"priceTicks\":100,\"quantityTicks\":10}],"
            + "\"sellSteps\":[]}";
        var content = new StringContent(oversizedJson, Encoding.UTF8, "application/json");
        var resp = await host.Client.PostAsync("/auction/bid", content, ct);

        // Acceptance: anything except 200 is fine — Kestrel commonly returns
        // 400 (BadRequest) or 413 (PayloadTooLarge) depending on whether the
        // size violation is detected at header or body parse time.
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }
}
