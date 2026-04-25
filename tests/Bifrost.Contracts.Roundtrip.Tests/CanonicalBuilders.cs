using Google.Protobuf;
using Bifrost.Contracts.Auction;
using Bifrost.Contracts.Events;
using Bifrost.Contracts.Market;
using Bifrost.Contracts.Mc;
using Bifrost.Contracts.Round;
using Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Roundtrip.Tests;

/// <summary>
/// Canonical, fully-populated message builders for CONT-04 round-trip coverage.
///
/// Every field of every top-level message and every oneof variant gets a
/// deterministic non-zero sentinel so protobuf's default-value-is-zero
/// semantics cannot hide a wire bug. Enum fields never use *_UNSPECIFIED.
///
/// Every entry in <see cref="EveryRoundtripTarget"/> corresponds 1:1 with a
/// row in contracts/roundtrip/harness.py's TYPE_MAP; the test driver matches
/// them by string identity.
///
/// Subprocess contract (RoundtripTheories.cs):
///   - C# builds a message here, serialises to bytes, writes to a tempfile.
///   - `uv run python contracts/roundtrip/harness.py --in &lt;tmpIn&gt;
///     --type &lt;TypeName&gt; --out &lt;tmpOut&gt;` parses + re-serialises.
///   - Test asserts byte equality between the C# canonical bytes and the
///     Python-re-serialised bytes — closing both directions of the wire in
///     one pass per target (Python that can parse-and-re-emit identical bytes
///     proves C#-emit and Python-emit are wire-compatible).
/// </summary>
public static class CanonicalBuilders
{
    // --- market.proto ---

    public static Instrument BuildInstrument() => new()
    {
        InstrumentId = "DE.Hour.2026-04-23T10:00",
        DeliveryArea = "DE",
        DeliveryPeriodStartNs = 1_745_400_000_000_000_000L,
        DeliveryPeriodEndNs = 1_745_403_600_000_000_000L,
        ProductType = ProductType.Hour,
    };

    public static BookLevel BuildBookLevel() => new()
    {
        PriceTicks = 42_000_000L,
        QuantityTicks = 50_000L,
        OrderCount = 7,
    };

    public static BookView BuildBookView()
    {
        var bv = new BookView
        {
            Sequence = 123_456L,
            TimestampNs = 1_745_400_000_000_000_001L,
        };
        bv.Bids.Add(BuildBookLevel());
        bv.Asks.Add(BuildBookLevel());
        return bv;
    }

    // --- auction.proto ---

    public static BidStep BuildBidStep() => new()
    {
        PriceTicks = 10_000L,
        QuantityTicks = 500L,
    };

    public static BidMatrix BuildBidMatrix()
    {
        var bm = new BidMatrix
        {
            TeamName = "team-skadi",
            QuarterId = "DE.Q.2026-04-23T10:00",
        };
        bm.BuySteps.Add(BuildBidStep());
        bm.SellSteps.Add(BuildBidStep());
        return bm;
    }

    public static ClearingResult BuildClearingResult() => new()
    {
        QuarterId = "DE.Q.2026-04-23T10:00",
        ClearingPriceTicks = 41_500_000L,
        AwardedQuantityTicks = 25_000L,
        TeamName = "team-skadi",
    };

    // --- round.proto ---

    public static RoundState BuildRoundState() => new()
    {
        State = State.RoundOpen,
        RoundNumber = 3,
        // ScenarioSeed intentionally 0 per ORC-05 (hidden during scored rounds);
        // this round-trips fine — proto default-zero serialises as empty-bytes
        // on both sides identically.
        ScenarioSeed = 0L,
        TransitionNs = 1_745_400_000_000_000_005L,
        ExpectedNextTransitionNs = 1_745_400_600_000_000_000L,
    };

    // --- events.proto (Event wrapper with each oneof variant) ---
    // The standalone payload messages (RegimeChange etc.) are exercised through
    // these wrappers — a bug in any nested field serialisation fails here.

    public static Event BuildEventRegimeChange() => new()
    {
        TimestampNs = 1_745_400_100_000_000_001L,
        Severity = Severity.Info,
        RegimeChange = new RegimeChange
        {
            From = Regime.Calm,
            To = Regime.Volatile,
            McForced = true,
        },
    };

    public static Event BuildEventForecastRevision() => new()
    {
        TimestampNs = 1_745_400_100_000_000_002L,
        Severity = Severity.Warn,
        ForecastRevision = new ForecastRevision
        {
            NewForecastPriceTicks = 44_200_000L,
            Reason = "mc_revise",
        },
    };

    public static Event BuildEventNews() => new()
    {
        TimestampNs = 1_745_400_100_000_000_003L,
        Severity = Severity.Info,
        News = new News
        {
            Text = "Grid operator announces redispatch window",
            LibraryKey = "news.redispatch.v1",
        },
    };

    public static Event BuildEventMarketAlert() => new()
    {
        TimestampNs = 1_745_400_100_000_000_004L,
        Severity = Severity.Warn,
        MarketAlert = new MarketAlert
        {
            Text = "Liquidity thin on DE.Q.2026-04-23T10:00",
        },
    };

    public static Event BuildEventConfigChange() => new()
    {
        TimestampNs = 1_745_400_100_000_000_005L,
        Severity = Severity.Info,
        ConfigChange = new ConfigChange
        {
            Path = "guards.otr.max_ratio",
            OldValue = "5.0",
            NewValue = "8.0",
        },
    };

    public static Event BuildEventPhysicalShock(int quarterIndex = 2) => new()
    {
        TimestampNs = 1_745_400_100_000_000_006L,
        Severity = Severity.Urgent,
        PhysicalShock = new PhysicalShock
        {
            Mw = -500,
            Label = "Gen-Trip-B1",
            Persistence = ShockPersistence.Round,
            QuarterIndex = quarterIndex,
        },
    };

    // Bare PhysicalShock (unwrapped from Event) — proves quarter_index round-trips independently.
    public static PhysicalShock BuildPhysicalShock(int quarterIndex = 2) => new()
    {
        Mw = -500,
        Label = "Gen-Trip-B1",
        Persistence = ShockPersistence.Round,
        QuarterIndex = quarterIndex,
    };

    // ImbalancePrint — public per-QH realized imbalance print, broadcast at Gate.
    public static ImbalancePrint BuildImbalancePrint() => new()
    {
        RoundNumber = 42,
        Instrument = BuildInstrument(),
        QuarterIndex = 2,
        PImbTicks = 5_200_000L,
        ATotalTicks = -30_000_000L,
        APhysicalTicks = -30_000_000L,
        Regime = Regime.Volatile,
        TimestampNs = 1_745_400_000_000_000_109L,
    };

    // --- strategy.proto: StrategyCommand oneof ---

    public static StrategyCommand BuildStrategyCommandRegister() => new()
    {
        Register = new Register
        {
            TeamName = "team-skadi",
            LastSeenSequence = 42L,
        },
    };

    public static StrategyCommand BuildStrategyCommandOrderSubmit() => new()
    {
        OrderSubmit = new OrderSubmit
        {
            ClientId = "client-001",
            Instrument = BuildInstrument(),
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            PriceTicks = 42_000_000L,
            QuantityTicks = 50_000L,
            DisplaySliceTicks = 10_000L,
            ClientOrderId = "co-12345",
        },
    };

    public static StrategyCommand BuildStrategyCommandOrderCancel() => new()
    {
        OrderCancel = new OrderCancel
        {
            ClientId = "client-001",
            OrderId = 999_111L,
            Instrument = BuildInstrument(),
        },
    };

    public static StrategyCommand BuildStrategyCommandOrderReplace() => new()
    {
        OrderReplace = new OrderReplace
        {
            ClientId = "client-001",
            OrderId = 999_111L,
            NewPriceTicks = 43_000_000L,
            NewQuantityTicks = 60_000L,
            Instrument = BuildInstrument(),
        },
    };

    public static StrategyCommand BuildStrategyCommandBidMatrixSubmit() => new()
    {
        BidMatrixSubmit = new BidMatrixSubmit
        {
            Matrix = BuildBidMatrix(),
        },
    };

    // --- strategy.proto: MarketEvent oneof (11 variants) ---

    public static MarketEvent BuildMarketEventRegisterAck() => new()
    {
        Sequence = 1L,
        TimestampNs = 1_745_400_000_000_000_101L,
        RegisterAck = new RegisterAck
        {
            ClientId = "client-001",
            CurrentRoundState = BuildRoundState(),
            ResumedFromSequence = 42L,
            ReregisterRequired = false,
        },
    };

    public static MarketEvent BuildMarketEventBookUpdate() => new()
    {
        Sequence = 2L,
        TimestampNs = 1_745_400_000_000_000_102L,
        BookUpdate = new BookUpdate
        {
            Instrument = BuildInstrument(),
            Book = BuildBookView(),
        },
    };

    public static MarketEvent BuildMarketEventTrade() => new()
    {
        Sequence = 3L,
        TimestampNs = 1_745_400_000_000_000_103L,
        Trade = new Trade
        {
            Instrument = BuildInstrument(),
            TradeId = 77_777L,
            PriceTicks = 42_500_000L,
            QuantityTicks = 15_000L,
            AggressorSide = Side.Sell,
            Sequence = 3L,
        },
    };

    public static MarketEvent BuildMarketEventForecastUpdate() => new()
    {
        Sequence = 4L,
        TimestampNs = 1_745_400_000_000_000_104L,
        ForecastUpdate = new ForecastUpdate
        {
            ForecastPriceTicks = 44_200_000L,
            HorizonNs = 1_745_400_600_000_000_000L,
        },
    };

    public static MarketEvent BuildMarketEventPublicEvent() => new()
    {
        Sequence = 5L,
        TimestampNs = 1_745_400_000_000_000_105L,
        PublicEvent = BuildEventPhysicalShock(),
    };

    public static MarketEvent BuildMarketEventOrderAck() => new()
    {
        Sequence = 6L,
        TimestampNs = 1_745_400_000_000_000_106L,
        OrderAck = new OrderAck
        {
            ClientOrderId = "co-12345",
            OrderId = 999_111L,
            Instrument = BuildInstrument(),
        },
    };

    public static MarketEvent BuildMarketEventOrderReject() => new()
    {
        Sequence = 7L,
        TimestampNs = 1_745_400_000_000_000_107L,
        OrderReject = new OrderReject
        {
            ClientOrderId = "co-12345",
            Reason = RejectReason.MaxNotional,
            Detail = "notional cap exceeded",
        },
    };

    public static MarketEvent BuildMarketEventFill() => new()
    {
        Sequence = 8L,
        TimestampNs = 1_745_400_000_000_000_108L,
        Fill = new Fill
        {
            ClientId = "client-001",
            Instrument = BuildInstrument(),
            OrderId = 999_111L,
            TradeId = 77_777L,
            PriceTicks = 42_500_000L,
            FilledQuantityTicks = 15_000L,
            RemainingQuantityTicks = 35_000L,
            Side = Side.Buy,
            IsAggressor = true,
            FeeTicks = 250L,
        },
    };

    public static MarketEvent BuildMarketEventRoundState() => new()
    {
        Sequence = 9L,
        TimestampNs = 1_745_400_000_000_000_109L,
        RoundState = BuildRoundState(),
    };

    public static MarketEvent BuildMarketEventScorecard() => new()
    {
        Sequence = 10L,
        TimestampNs = 1_745_400_000_000_000_110L,
        Scorecard = new Scorecard
        {
            RoundNumber = 3,
            TradePnlTicks = 1_250_000L,
            ImbalancePnlTicks = -300_000L,
            FeesTicks = 2_500L,
            OtrPenaltyTicks = 0L,
            TotalTicks = 947_500L,
        },
    };

    public static MarketEvent BuildMarketEventPositionSnapshot() => new()
    {
        Sequence = 11L,
        TimestampNs = 1_745_400_000_000_000_111L,
        PositionSnapshot = new PositionSnapshot
        {
            Instrument = BuildInstrument(),
            NetPositionTicks = 20_000L,
            AveragePriceTicks = 42_100_000L,
            OpenOrdersNotionalTicks = 5_000_000L,
        },
    };

    public static MarketEvent BuildMarketEventImbalancePrint() => new()
    {
        Sequence = 12L,
        TimestampNs = 1_745_400_000_000_000_112L,
        ImbalancePrint = BuildImbalancePrint(),
    };

    // --- mc.proto: McCommand oneof (21 variants) ---
    // Each wraps envelope fields (operator_host / confirm / dry_run) so those
    // also get exercised on every row.

    private static McCommand BaseMcCommand() => new()
    {
        OperatorHost = "mc-01",
        Confirm = true,
        DryRun = false,
    };

    public static McCommand BuildMcCommandAuctionOpen()
    {
        var c = BaseMcCommand();
        c.AuctionOpen = new AuctionOpenCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandAuctionClose()
    {
        var c = BaseMcCommand();
        c.AuctionClose = new AuctionCloseCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandRoundStart()
    {
        var c = BaseMcCommand();
        c.RoundStart = new RoundStartCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandRoundEnd()
    {
        var c = BaseMcCommand();
        c.RoundEnd = new RoundEndCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandGate()
    {
        var c = BaseMcCommand();
        c.Gate = new GateCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandSettle()
    {
        var c = BaseMcCommand();
        c.Settle = new SettleCmd { RoundNumber = 3 };
        return c;
    }

    public static McCommand BuildMcCommandNextRound()
    {
        // NextRoundCmd has no fields — but setting the oneof case to empty
        // message is still a non-default behaviour vs. an unset command.
        var c = BaseMcCommand();
        c.NextRound = new NextRoundCmd();
        return c;
    }

    public static McCommand BuildMcCommandPause()
    {
        var c = BaseMcCommand();
        c.Pause = new PauseCmd();
        return c;
    }

    public static McCommand BuildMcCommandResume()
    {
        var c = BaseMcCommand();
        c.Resume = new ResumeCmd();
        return c;
    }

    public static McCommand BuildMcCommandAbort()
    {
        var c = BaseMcCommand();
        c.Abort = new AbortCmd { Reason = "operator-abort" };
        return c;
    }

    public static McCommand BuildMcCommandForecastRevise()
    {
        var c = BaseMcCommand();
        c.ForecastRevise = new ForecastReviseCmd
        {
            NewForecastPriceTicks = 44_200_000L,
            Reason = "mc_revise",
        };
        return c;
    }

    public static McCommand BuildMcCommandRegimeForce()
    {
        var c = BaseMcCommand();
        c.RegimeForce = new RegimeForceCmd { Regime = Regime.Shock };
        return c;
    }

    public static McCommand BuildMcCommandNewsFire()
    {
        var c = BaseMcCommand();
        c.NewsFire = new NewsFireCmd { LibraryKey = "news.redispatch.v1" };
        return c;
    }

    public static McCommand BuildMcCommandNewsPublish()
    {
        var c = BaseMcCommand();
        c.NewsPublish = new NewsPublishCmd { Text = "Ad-hoc operator bulletin" };
        return c;
    }

    public static McCommand BuildMcCommandAlertUrgent()
    {
        var c = BaseMcCommand();
        c.AlertUrgent = new AlertUrgentCmd { Text = "Urgent alert body" };
        return c;
    }

    public static McCommand BuildMcCommandPhysicalShock(int quarterIndex = 2)
    {
        var c = BaseMcCommand();
        c.PhysicalShock = new PhysicalShockCmd
        {
            Mw = -500,
            Label = "Gen-Trip-B1",
            Persistence = ShockPersistence.Transient,
            QuarterIndex = quarterIndex,
        };
        return c;
    }

    // Bare PhysicalShockCmd (unwrapped from McCommand) — proves quarter_index round-trips independently.
    public static PhysicalShockCmd BuildPhysicalShockCmd(int quarterIndex = 2) => new()
    {
        Mw = -500,
        Label = "Gen-Trip-B1",
        Persistence = ShockPersistence.Transient,
        QuarterIndex = quarterIndex,
    };

    public static McCommand BuildMcCommandTeamKick()
    {
        var c = BaseMcCommand();
        c.TeamKick = new TeamKickCmd { TeamName = "team-skadi" };
        return c;
    }

    public static McCommand BuildMcCommandTeamReset()
    {
        var c = BaseMcCommand();
        c.TeamReset = new TeamResetCmd { TeamName = "team-skadi" };
        return c;
    }

    public static McCommand BuildMcCommandConfigSet()
    {
        var c = BaseMcCommand();
        c.ConfigSet = new ConfigSetCmd
        {
            Path = "guards.otr.max_ratio",
            Value = "8.0",
        };
        return c;
    }

    public static McCommand BuildMcCommandLeaderboardReveal()
    {
        var c = BaseMcCommand();
        c.LeaderboardReveal = new LeaderboardRevealCmd();
        return c;
    }

    public static McCommand BuildMcCommandEventEnd()
    {
        var c = BaseMcCommand();
        c.EventEnd = new EventEndCmd();
        return c;
    }

    public static McCommandResult BuildMcCommandResult() => new()
    {
        Success = true,
        Message = "accepted",
        NewState = BuildRoundState(),
        DryRunPayload = "preview-body",
    };

    // WatchRoundState server-streaming resume-cursor request. last_seen_transition_ns
    // is the monotonic nanosecond timestamp the client last observed; the server
    // replays from there forward if the value is within the in-memory ring buffer.
    public static WatchRoundStateRequest BuildWatchRoundStateRequest() => new()
    {
        LastSeenTransitionNs = 1_714_516_800_000_000_001L,
    };

    /// <summary>
    /// The full CONT-04 coverage list — every top-level message + every oneof
    /// variant (D-10). Each entry is (TypeName, canonicalBytes). TypeName maps
    /// 1:1 with harness.py's TYPE_MAP; CI fails on any mismatch.
    /// </summary>
    public static IEnumerable<(string TypeName, byte[] Bytes)> EveryRoundtripTarget()
    {
        // --- market.proto (4) ---
        yield return ("market.Instrument", BuildInstrument().ToByteArray());
        yield return ("market.BookLevel", BuildBookLevel().ToByteArray());
        yield return ("market.BookView", BuildBookView().ToByteArray());
        yield return ("market.ImbalancePrint", BuildImbalancePrint().ToByteArray());

        // --- auction.proto (3) ---
        yield return ("auction.BidStep", BuildBidStep().ToByteArray());
        yield return ("auction.BidMatrix", BuildBidMatrix().ToByteArray());
        yield return ("auction.ClearingResult", BuildClearingResult().ToByteArray());

        // --- round.proto (1) ---
        yield return ("round.RoundState", BuildRoundState().ToByteArray());

        // --- events.proto: Event oneof (6) + bare PhysicalShock (1) ---
        yield return ("events.Event.RegimeChange", BuildEventRegimeChange().ToByteArray());
        yield return ("events.Event.ForecastRevision", BuildEventForecastRevision().ToByteArray());
        yield return ("events.Event.News", BuildEventNews().ToByteArray());
        yield return ("events.Event.MarketAlert", BuildEventMarketAlert().ToByteArray());
        yield return ("events.Event.ConfigChange", BuildEventConfigChange().ToByteArray());
        yield return ("events.Event.PhysicalShock", BuildEventPhysicalShock().ToByteArray());
        yield return ("events.PhysicalShock", BuildPhysicalShock().ToByteArray());

        // --- strategy.proto: StrategyCommand oneof (5) ---
        yield return ("strategy.StrategyCommand.Register", BuildStrategyCommandRegister().ToByteArray());
        yield return ("strategy.StrategyCommand.OrderSubmit", BuildStrategyCommandOrderSubmit().ToByteArray());
        yield return ("strategy.StrategyCommand.OrderCancel", BuildStrategyCommandOrderCancel().ToByteArray());
        yield return ("strategy.StrategyCommand.OrderReplace", BuildStrategyCommandOrderReplace().ToByteArray());
        yield return ("strategy.StrategyCommand.BidMatrixSubmit", BuildStrategyCommandBidMatrixSubmit().ToByteArray());

        // --- strategy.proto: MarketEvent oneof (12) ---
        yield return ("strategy.MarketEvent.RegisterAck", BuildMarketEventRegisterAck().ToByteArray());
        yield return ("strategy.MarketEvent.BookUpdate", BuildMarketEventBookUpdate().ToByteArray());
        yield return ("strategy.MarketEvent.Trade", BuildMarketEventTrade().ToByteArray());
        yield return ("strategy.MarketEvent.ForecastUpdate", BuildMarketEventForecastUpdate().ToByteArray());
        yield return ("strategy.MarketEvent.PublicEvent", BuildMarketEventPublicEvent().ToByteArray());
        yield return ("strategy.MarketEvent.OrderAck", BuildMarketEventOrderAck().ToByteArray());
        yield return ("strategy.MarketEvent.OrderReject", BuildMarketEventOrderReject().ToByteArray());
        yield return ("strategy.MarketEvent.Fill", BuildMarketEventFill().ToByteArray());
        yield return ("strategy.MarketEvent.RoundState", BuildMarketEventRoundState().ToByteArray());
        yield return ("strategy.MarketEvent.Scorecard", BuildMarketEventScorecard().ToByteArray());
        yield return ("strategy.MarketEvent.PositionSnapshot", BuildMarketEventPositionSnapshot().ToByteArray());
        yield return ("strategy.MarketEvent.ImbalancePrint", BuildMarketEventImbalancePrint().ToByteArray());

        // --- mc.proto: McCommand oneof (21) ---
        yield return ("mc.McCommand.AuctionOpen", BuildMcCommandAuctionOpen().ToByteArray());
        yield return ("mc.McCommand.AuctionClose", BuildMcCommandAuctionClose().ToByteArray());
        yield return ("mc.McCommand.RoundStart", BuildMcCommandRoundStart().ToByteArray());
        yield return ("mc.McCommand.RoundEnd", BuildMcCommandRoundEnd().ToByteArray());
        yield return ("mc.McCommand.Gate", BuildMcCommandGate().ToByteArray());
        yield return ("mc.McCommand.Settle", BuildMcCommandSettle().ToByteArray());
        yield return ("mc.McCommand.NextRound", BuildMcCommandNextRound().ToByteArray());
        yield return ("mc.McCommand.Pause", BuildMcCommandPause().ToByteArray());
        yield return ("mc.McCommand.Resume", BuildMcCommandResume().ToByteArray());
        yield return ("mc.McCommand.Abort", BuildMcCommandAbort().ToByteArray());
        yield return ("mc.McCommand.ForecastRevise", BuildMcCommandForecastRevise().ToByteArray());
        yield return ("mc.McCommand.RegimeForce", BuildMcCommandRegimeForce().ToByteArray());
        yield return ("mc.McCommand.NewsFire", BuildMcCommandNewsFire().ToByteArray());
        yield return ("mc.McCommand.NewsPublish", BuildMcCommandNewsPublish().ToByteArray());
        yield return ("mc.McCommand.AlertUrgent", BuildMcCommandAlertUrgent().ToByteArray());
        yield return ("mc.McCommand.PhysicalShock", BuildMcCommandPhysicalShock().ToByteArray());
        yield return ("mc.McCommand.TeamKick", BuildMcCommandTeamKick().ToByteArray());
        yield return ("mc.McCommand.TeamReset", BuildMcCommandTeamReset().ToByteArray());
        yield return ("mc.McCommand.ConfigSet", BuildMcCommandConfigSet().ToByteArray());
        yield return ("mc.McCommand.LeaderboardReveal", BuildMcCommandLeaderboardReveal().ToByteArray());
        yield return ("mc.McCommand.EventEnd", BuildMcCommandEventEnd().ToByteArray());

        // --- mc.proto: bare PhysicalShockCmd standalone (1) + McCommandResult standalone (1) ---
        yield return ("mc.PhysicalShockCmd", BuildPhysicalShockCmd().ToByteArray());
        yield return ("mc.McCommandResult", BuildMcCommandResult().ToByteArray());

        // --- mc.proto: OrchestratorService request envelope (1) ---
        yield return ("mc.WatchRoundStateRequest", BuildWatchRoundStateRequest().ToByteArray());
    }
}
