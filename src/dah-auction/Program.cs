using System.Globalization;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction;
using Bifrost.DahAuction.Commands;
using Bifrost.DahAuction.Rabbit;
using Bifrost.DahAuction.State;
using Bifrost.DahAuction.Validation;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Time;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

var builder = WebApplication.CreateBuilder(args);

// ---------- JSON source-generation for the three auction DTOs ----------
// Inserted at index 0 of the chain so the source-generated metadata wins
// over any reflection-based fallback. Plan 04 authored
// AuctionJsonSerializerContext as an internal partial class targeting
// BidMatrixDto / BidStepDto / ClearingResultDto.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AuctionJsonSerializerContext.Default);
});

// ---------- RabbitMQ connection (Polly-retried; shared across publishers) ----------
// Tolerates the cold-boot race where the dah-auction container starts polling
// before rabbit's AMQP listener is ready. Mirrors the Quoter Program.cs
// composition shape verbatim.
var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
var factory = new ConnectionFactory
{
    HostName = rabbitConfig["Host"] ?? "rabbitmq",
    Port = int.Parse(rabbitConfig["Port"] ?? "5672", CultureInfo.InvariantCulture),
    UserName = rabbitConfig["Username"] ?? "guest",
    Password = rabbitConfig["Password"] ?? "guest",
};

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Bifrost.DahAuction.Startup");
var pipeline = RabbitMqResilience.CreateConnectionPipeline(startupLogger);
var connection = await pipeline.ExecuteAsync(
    async ct => await factory.CreateConnectionAsync("bifrost-dah-auction", ct),
    CancellationToken.None);
builder.Services.AddSingleton(connection);

// Declare the new bifrost.auction direct exchange once on boot. Idempotent;
// safe across restarts. The bifrost.public exchange used by the audit
// emissions is already declared by the central exchange service on its own
// boot, so this service does NOT redeclare it here.
await using (var declChannel = await connection.CreateChannelAsync())
{
    await AuctionRabbitTopology.DeclareAuctionExchangeAsync(declChannel);
}

// ---------- Clock seam ----------
// Every clock access goes through IClock; tests inject FakeTimeProvider.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// ---------- Round-state seam ----------
// Production ConfigRoundStateSource reads RoundState:Initial from
// appsettings.json (default IterationOpen for DAH; see appsettings).
// Integration tests override this binding with MockRoundStateSource.
builder.Services.AddSingleton<IRoundStateSource>(_ =>
    new ConfigRoundStateSource(
        Enum.Parse<RoundStateEnum>(builder.Configuration["RoundState:Initial"] ?? "IterationOpen")));

// ---------- InstrumentRegistry ----------
// DAH does not need matching-engine behaviour; the validator only queries
// InstrumentRegistry.GetQuarterInstruments() which reads the engine keys.
// Build minimal MatchingEngines over the 5 TradingCalendar instruments
// using the same factory recipe BidMatrixValidatorTests + InstrumentRegistryTests
// use for their own fixtures.
builder.Services.AddSingleton<InstrumentRegistry>(_ =>
{
    var instruments = TradingCalendar.GenerateInstruments();
    var engines = instruments
        .Select(id => new MatchingEngine(new OrderBook(id), new MonotonicSequenceGenerator()))
        .ToList();
    return new InstrumentRegistry(engines);
});

// ---------- Validator ----------
// MaxStepsPerSide and ChannelCapacity bind from appsettings.json
// (DahAuction section); compile-time defaults match Plan 04 conventions.
builder.Services.AddSingleton<BidMatrixValidator>(sp =>
    new BidMatrixValidator(
        sp.GetRequiredService<InstrumentRegistry>(),
        builder.Configuration.GetValue<int>("DahAuction:MaxStepsPerSide", 20)));

// ---------- Channel<IAuctionCommand> ----------
// Bounded with FullMode=Wait so a saturated channel back-pressures inbound
// HTTP handlers (await channel.Writer.WriteAsync) instead of growing memory.
// Single reader is the AuctionWriteLoop drain body; multiple writers are
// the HTTP endpoint handlers and the round-state OnChange subscriber.
var channelCapacity = builder.Configuration.GetValue<int>("DahAuction:ChannelCapacity", 256);
builder.Services.AddSingleton(_ =>
    Channel.CreateBounded<IAuctionCommand>(new BoundedChannelOptions(channelCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    }));

// ---------- Publisher stack ----------
// BufferedEventPublisher wraps RabbitMqEventPublisher on its own dedicated
// IChannel (events bus). AuctionPublisher owns a SECOND IChannel for the
// direct bifrost.auction clearing fan-out. Two separate AMQP channels are
// required because RabbitMQ.Client 7.x channels are NOT thread-safe and the
// buffered publisher's drain task must not share with the actor loop's
// outbound publish path.
builder.Services.AddSingleton<RabbitMqEventPublisher>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var eventsChannel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    var clock = sp.GetRequiredService<IClock>();
    return new RabbitMqEventPublisher(eventsChannel, clock);
});
builder.Services.AddSingleton<BufferedEventPublisher>(sp =>
    new BufferedEventPublisher(
        sp.GetRequiredService<RabbitMqEventPublisher>(),
        sp.GetService<ILogger<BufferedEventPublisher>>()));

builder.Services.AddSingleton<AuctionPublisher>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var directChannel = conn.CreateChannelAsync().GetAwaiter().GetResult();
    return new AuctionPublisher(
        directChannel,
        sp.GetRequiredService<BufferedEventPublisher>(),
        sp.GetRequiredService<IClock>());
});

// ---------- Hosted services ----------
// AuctionWriteLoop is the single-writer actor; StartupLogger writes the
// /tmp/bifrost-ready sentinel for the docker-compose healthcheck.
builder.Services.AddHostedService<AuctionWriteLoop>();
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();

// ---------- Health endpoint ----------
// Lightweight complement to the sentinel-file healthcheck. Always returns
// 200 once Kestrel is listening; useful for compose smoke probes that hit
// the HTTP surface directly.
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

// ---------- POST /auction/bid ----------
// Validation -> channel hand-off -> await TCS completion -> 200/400.
// The Accepts metadata declared below is what makes Kestrel return
// HTTP 415 Unsupported Media Type for non-JSON bodies (D-10 guardrail).
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
            return Results.BadRequest(new { code = validated.Error!.Code, detail = validated.Error!.Detail });
        }

        var completion = new TaskCompletionSource<SubmitBidResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new SubmitBidCommand(validated.Value!, completion);
        await channel.Writer.WriteAsync(command, ct);
        var result = await completion.Task;

        if (result.Accepted)
        {
            return Results.Ok(new { accepted = true });
        }
        return Results.BadRequest(new { code = result.RejectCode, detail = result.RejectDetail });
    })
    .Accepts<BidMatrixDto>("application/json")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status415UnsupportedMediaType);

await app.RunAsync();
