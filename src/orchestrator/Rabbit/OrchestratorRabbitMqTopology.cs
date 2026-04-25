using System.Text;
using RabbitMQ.Client;

namespace Bifrost.Orchestrator.Rabbit;

/// <summary>
/// Orchestrator's RabbitMQ topology declarations + routing-key helpers. The
/// orchestrator publishes on four topic exchanges:
///
///   - <see cref="RoundExchange"/>  ("bifrost.round.v1")  — phase-owned; every
///     RoundStateChanged fan-out publish lands here on
///     <c>round.state.{state_snake}</c>.
///   - <see cref="McAuditExchange"/> ("bifrost.mc.v1")    — phase-owned; the
///     <c>mc.command.{cmd_snake}</c> audit stream the recorder binds
///     <c>mc.command.#</c> to.
///   - <see cref="EventsExchange"/> ("bifrost.events.v1") — first producer
///     (Phase 02 RecorderTopology registered the name but declared no runtime
///     binding). Routing keys: <c>events.news</c>, <c>events.market_alert</c>,
///     <c>events.config_change</c>.
///   - <see cref="QuoterMcExchange"/> ("bifrost.mc")      — Phase 03-owned
///     (quoter's inbound MC fan-out). The orchestrator routes RegimeForceCmd
///     here on <see cref="QuoterMcRegimeRoutingKey"/> ("mc.regime.force")
///     per the D-14 amendment: orchestrator ROUTES, quoter EMITS the public
///     <c>Event.RegimeChange</c>. NOT <c>bifrost.mc.v1</c> — that's the
///     audit-only exchange.
///
/// All four declarations use <c>durable=true, autoDelete=false, arguments=null</c>
/// — matching the Phase 03 / Phase 02 declare-args-verbatim convention.
/// RabbitMQ returns a 406 connection-level error if any producer declares the
/// same exchange with mismatched args, so this class re-declares
/// <c>bifrost.mc</c> with exactly the Phase 03 quoter shape.
/// </summary>
public sealed class OrchestratorRabbitMqTopology
{
    // Phase 06-owned exchanges.
    public const string RoundExchange = "bifrost.round.v1";
    public const string McAuditExchange = "bifrost.mc.v1";

    // Phase 06 is the first runtime producer on this name (Phase 02
    // RecorderTopology registered the string but never declared it).
    public const string EventsExchange = "bifrost.events.v1";

    // Phase 03-owned constants — REUSE verbatim for D-14 RegimeForce routing.
    // Do NOT invent a bifrost.mc.v1-suffixed synonym; the quoter binds its
    // queue to this exact exchange name and routing key.
    public const string QuoterMcExchange = "bifrost.mc";
    public const string QuoterMcRegimeRoutingKey = "mc.regime.force";

    // Routing-key constants for the events the orchestrator directly emits
    // on bifrost.events.v1.
    public const string EventsNewsRoutingKey = "events.news";
    public const string EventsMarketAlertRoutingKey = "events.market_alert";
    public const string EventsConfigChangeRoutingKey = "events.config_change";

    // Used when a NewsFireCmd resolves to a canned-library entry that carries
    // an optional shock payload — the orchestrator emits the news envelope on
    // EventsNewsRoutingKey AND a PhysicalShockPayload envelope on this key.
    // Underscore-form matches the SPEC's wire-routing convention (see SPEC
    // Req 9 acceptance test) and the sibling routing keys above; this is
    // distinct from the imbalance simulator's pre-existing tentative
    // dot-form binding for operator-injected PhysicalShockCmds, which is a
    // Phase 04 follow-up rename concern.
    public const string EventsPhysicalShockRoutingKey = "events.physical_shock";

    private readonly IChannel _channel;

    public OrchestratorRabbitMqTopology(IChannel channel)
    {
        _channel = channel;
    }

    /// <summary>
    /// Idempotently declare all four exchanges. Safe to call multiple times in
    /// the same process (RabbitMQ exchange-declare is idempotent if args
    /// match). Safe across processes as long as every producer uses identical
    /// args — the quoter's <c>bifrost.mc</c> declare uses the same shape.
    /// </summary>
    public async Task DeclareAsync(CancellationToken ct = default)
    {
        await _channel.ExchangeDeclareAsync(
            RoundExchange, ExchangeType.Topic,
            durable: true, autoDelete: false, arguments: null,
            cancellationToken: ct);

        await _channel.ExchangeDeclareAsync(
            McAuditExchange, ExchangeType.Topic,
            durable: true, autoDelete: false, arguments: null,
            cancellationToken: ct);

        await _channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic,
            durable: true, autoDelete: false, arguments: null,
            cancellationToken: ct);

        // Phase 03 already declares this exchange from the quoter side; this
        // declare is redundant-but-safe (RabbitMQ idempotent if args match).
        // Keeps the orchestrator startup self-contained — downstream services
        // can come up in any order and the exchange will be there.
        await _channel.ExchangeDeclareAsync(
            QuoterMcExchange, ExchangeType.Topic,
            durable: true, autoDelete: false, arguments: null,
            cancellationToken: ct);
    }

    /// <summary>
    /// Build the <c>round.state.{stateSnakeCase}</c> routing key for a
    /// RoundStateChanged publish on <see cref="RoundExchange"/>.
    /// </summary>
    public static string RoundStateRoutingKey(string stateSnakeCase) =>
        $"round.state.{stateSnakeCase}";

    /// <summary>
    /// Build the <c>mc.command.{commandSnakeCase}</c> routing key for an
    /// McCommandLog publish on <see cref="McAuditExchange"/>.
    /// </summary>
    public static string McCommandRoutingKey(string commandSnakeCase) =>
        $"mc.command.{commandSnakeCase}";

    /// <summary>
    /// Convert a PascalCase identifier (enum name, command variant name) to
    /// snake_case for routing-key construction. Examples:
    /// <c>IterationOpen</c> → <c>iteration_open</c>; <c>AuctionOpen</c> →
    /// <c>auction_open</c>; <c>Aborted</c> → <c>aborted</c>;
    /// <c>NewsFire</c> → <c>news_fire</c>.
    /// </summary>
    public static string ToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
        {
            return pascalCase;
        }

        StringBuilder sb = new(pascalCase.Length + 4);
        for (int i = 0; i < pascalCase.Length; i++)
        {
            char c = pascalCase[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
