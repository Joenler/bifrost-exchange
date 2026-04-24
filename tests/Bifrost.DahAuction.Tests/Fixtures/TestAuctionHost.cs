using System.Threading.Channels;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction;
using Bifrost.DahAuction.Commands;
using Bifrost.DahAuction.Rabbit;
using Bifrost.DahAuction.State;
using Bifrost.DahAuction.Validation;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.DahAuction.Tests.Fixtures;

/// <summary>
/// In-process <see cref="WebApplication"/> harness for the DAH auction
/// integration tests. Spins up Kestrel on <c>http://127.0.0.1:0</c> (dynamic
/// port so parallel tests do not collide), wires the same DI graph as the
/// production composition root EXCEPT that:
/// <list type="bullet">
///   <item><see cref="IRoundStateSource"/> is the test-mutable
///         <see cref="MockRoundStateSource"/> exposed via <see cref="MockRoundState"/>.</item>
///   <item><see cref="IAuctionPublisher"/> is the in-memory
///         <see cref="TestAuctionPublisher"/> exposed via <see cref="FakePublisher"/>.</item>
/// </list>
/// No RabbitMQ connection, no <c>BufferedEventPublisher</c> drain thread —
/// the test publisher captures every emission synchronously.
/// </summary>
public sealed class TestAuctionHost : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public MockRoundStateSource MockRoundState { get; }
    public TestAuctionPublisher FakePublisher { get; }

    /// <summary>
    /// Snapshot of the captured publisher emissions in arrival order.
    /// </summary>
    public IReadOnlyList<CapturedAuctionMessage> CapturedMessages =>
        FakePublisher.Captured.ToList();

    private TestAuctionHost(
        WebApplication app,
        HttpClient client,
        MockRoundStateSource mock,
        TestAuctionPublisher pub)
    {
        App = app;
        Client = client;
        MockRoundState = mock;
        FakePublisher = pub;
    }

    /// <summary>
    /// Build, start, and return a fresh host. Each test should
    /// <c>await using</c> its own host so port + state are isolated.
    /// </summary>
    public static async Task<TestAuctionHost> StartAsync(
        RoundStateEnum initial = RoundStateEnum.IterationOpen)
    {
        var builder = WebApplication.CreateBuilder();
        // The dah-auction src project ships an appsettings.json pinning Kestrel
        // to http://+:8080, which gets copied to the test bin output through
        // the ProjectReference. Override that binding here with a dynamic
        // loopback port so multiple tests can run in parallel without colliding
        // and without binding to all interfaces. Both config keys are cleared
        // explicitly to make the override unambiguous regardless of which
        // resolution path Kestrel picks (Kestrel:Endpoints vs urls).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
            ["urls"] = "http://127.0.0.1:0",
        });

        // Source-generated JSON resolver — same registration the production
        // host uses so deserialization behaviour matches end-to-end.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(0, AuctionJsonSerializerContext.Default));

        var clock = new SystemClock();
        var mock = new MockRoundStateSource(clock, initial);
        var publisher = new TestAuctionPublisher();

        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRoundStateSource>(mock);
        builder.Services.AddSingleton<IAuctionPublisher>(publisher);

        builder.Services.AddSingleton<InstrumentRegistry>(_ =>
        {
            // Same factory recipe used by InstrumentRegistryTests +
            // BidMatrixValidatorTests + the production composition root.
            var instruments = TradingCalendar.GenerateInstruments();
            var engines = instruments
                .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
                .ToList();
            return new InstrumentRegistry(engines);
        });

        builder.Services.AddSingleton<BidMatrixValidator>(sp =>
            new BidMatrixValidator(sp.GetRequiredService<InstrumentRegistry>(), maxStepsPerSide: 20));

        builder.Services.AddSingleton(_ =>
            Channel.CreateBounded<IAuctionCommand>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }));

        builder.Services.AddHostedService<AuctionWriteLoop>();

        var app = builder.Build();

        // Replicate the production MapPost handler from the host's Program.cs
        // verbatim. The Accepts metadata is what makes Kestrel return HTTP
        // 415 Unsupported Media Type for non-JSON bodies.
        app.MapPost("/auction/bid",
            async Task<IResult> (
                BidMatrixDto candidate,
                BidMatrixValidator validator,
                Channel<IAuctionCommand> channel,
                CancellationToken ct) =>
            {
                var validated = validator.Validate(candidate);
                if (validated.IsError)
                {
                    return Results.BadRequest(new
                    {
                        code = validated.Error!.Code,
                        detail = validated.Error!.Detail,
                    });
                }

                var completion = new TaskCompletionSource<SubmitBidResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var command = new SubmitBidCommand(validated.Value!, completion);
                await channel.Writer.WriteAsync(command, ct);
                var result = await completion.Task;

                if (result.Accepted)
                {
                    return Results.Ok(new { accepted = true });
                }
                return Results.BadRequest(new
                {
                    code = result.RejectCode,
                    detail = result.RejectDetail,
                });
            })
            .Accepts<BidMatrixDto>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        await app.StartAsync();

        // Extract the bound URL so the HttpClient hits the dynamic port.
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        var boundUrl = addresses!.Addresses.First();

        var client = new HttpClient
        {
            BaseAddress = new Uri(boundUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        return new TestAuctionHost(app, client, mock, publisher);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await App.StopAsync();
        }
        catch
        {
            // Best-effort stop; the host may already be cancelled.
        }
        await App.DisposeAsync();
    }
}
