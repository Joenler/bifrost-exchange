using System.Globalization;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange;
using Bifrost.Exchange.Application;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// ---------- RabbitMQ connection (Polly-retried via shared resilience pipeline) ----------
var rabbitConfig = builder.Configuration.GetSection("RabbitMq");
var factory = new ConnectionFactory
{
    HostName = rabbitConfig["Host"] ?? "rabbitmq",
    Port = int.Parse(rabbitConfig["Port"] ?? "5672", CultureInfo.InvariantCulture),
    UserName = rabbitConfig["Username"] ?? "guest",
    Password = rabbitConfig["Password"] ?? "guest",
};

using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Bifrost.Exchange.Startup");
var pipeline = RabbitMqResilience.CreateConnectionPipeline(startupLogger);

var connection = await pipeline.ExecuteAsync(
    async ct => await factory.CreateConnectionAsync("bifrost-exchange", ct),
    CancellationToken.None);
var publishConnection = await pipeline.ExecuteAsync(
    async ct => await factory.CreateConnectionAsync("bifrost-exchange-publisher", ct),
    CancellationToken.None);
var publishChannel = await publishConnection.CreateChannelAsync();

builder.Services.AddSingleton(connection);
builder.Services.AddSingleton(publishChannel);

// ---------- Clock ----------
IClock clock = new SystemClock();
builder.Services.AddSingleton(clock);

// ---------- 5 static instruments + per-instrument MatchingEngine ----------
var instruments = TradingCalendar.GenerateInstruments();
var engines = new List<MatchingEngine>(instruments.Count);
foreach (var instrumentId in instruments)
{
    var book = new OrderBook(instrumentId);
    var seqGen = new MonotonicSequenceGenerator();
    engines.Add(new MatchingEngine(book, seqGen));
}
var registry = new InstrumentRegistry(engines);
builder.Services.AddSingleton(registry);

// ---------- Publisher stack (buffered wrapper over RabbitMQ) ----------
var rawPublisher = new RabbitMqEventPublisher(publishChannel, clock);
var publisher = new BufferedEventPublisher(rawPublisher);
builder.Services.AddSingleton<IEventPublisher>(publisher);

// ---------- ExchangeRules from config ----------
var rulesSection = builder.Configuration.GetSection("ExchangeRules");
var exchangeRules = new ExchangeRulesConfig(
    TickSize: long.Parse(rulesSection["TickSize"] ?? "1", CultureInfo.InvariantCulture),
    MinQuantity: decimal.Parse(rulesSection["MinQuantity"] ?? "0.1", CultureInfo.InvariantCulture),
    QuantityStep: decimal.Parse(rulesSection["QuantityStep"] ?? "0.1", CultureInfo.InvariantCulture),
    MakerFeeRate: decimal.Parse(rulesSection["MakerFeeRate"] ?? "0.01", CultureInfo.InvariantCulture),
    TakerFeeRate: decimal.Parse(rulesSection["TakerFeeRate"] ?? "0.02", CultureInfo.InvariantCulture),
    PriceScale: long.Parse(rulesSection["PriceScale"] ?? "10", CultureInfo.InvariantCulture));
builder.Services.AddSingleton(exchangeRules);

// ---------- RoundState seam (future orchestrator will swap to RabbitMqRoundStateSource) ----------
var initialRoundState = Enum.Parse<RoundState>(
    builder.Configuration["RoundState:Initial"] ?? "RoundOpen");
var roundStateSource = new ConfigRoundStateSource(initialRoundState);
builder.Services.AddSingleton<IRoundStateSource>(roundStateSource);

// ---------- Validator + publishers + service ----------
var seqTracker = new PublicSequenceTracker();
builder.Services.AddSingleton(seqTracker);

var orderValidator = new OrderValidator(exchangeRules, registry, clock, roundStateSource);
var bookPublisher = new BookPublisher(publisher, seqTracker);
var tradePublisher = new TradePublisher(publisher, seqTracker, exchangeRules);
var orderIdGen = new MonotonicSequenceGenerator();

var exchangeService = new ExchangeService(
    orderValidator,
    bookPublisher,
    tradePublisher,
    registry,
    publisher,
    seqTracker,
    orderIdGen,
    clock,
    exchangeRules);
builder.Services.AddSingleton(exchangeService);

// ---------- Hosted services ----------
builder.Services.AddHostedService<CommandConsumerService>();
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();
await app.RunAsync();
