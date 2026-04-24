#!/usr/bin/env python3
"""
CONT-04 polyglot round-trip subprocess harness.

Invoked by Bifrost.Contracts.Roundtrip.Tests (xUnit.v3 Theory driver) via:

    uv run --project contracts/roundtrip python contracts/roundtrip/harness.py \\
        --in  <tmp-bytes-in>    # canonical bytes emitted by C#
        --type <TYPE_MAP key>   # e.g. "strategy.StrategyCommand.OrderSubmit"
        --out <tmp-bytes-out>   # where to write Python-re-serialised bytes

The test asserts byte-equivalence between --in and --out: if Python can read
C#-produced bytes and re-emit them unchanged, the protobuf wire contract
holds in both directions (proto3 serialisation is deterministic under
ascending-tag-order field emission — the contract both runtimes follow).

Pitfall E (RESEARCH §12): this script MUST use only .ParseFromString and
.SerializeToString on the generated message class. Never attribute
assignment on nested oneof fields — that path silently drifts between
runtimes because protobuf 5.x pure-python reconstructs the field descriptor
table on each attribute write.

D-09: no .pb fixtures are committed. Every invocation of this harness
receives bytes built fresh from CanonicalBuilders.cs at test time.
"""

from __future__ import annotations

import argparse
import importlib
import sys
from pathlib import Path

# TYPE_MAP: CanonicalBuilders.EveryRoundtripTarget TypeName → (module, class).
#
# Module paths use the FLAT layout locked by Plan 01-05's build.sh
# (bifrost_contracts.<surface>.<surface>_pb2). Any new .proto top-level
# message or oneof variant MUST land a row here AND in CanonicalBuilders.cs
# — CI fails fast on the first KeyError otherwise.
TYPE_MAP: dict[str, tuple[str, str]] = {
    # --- market.proto (4) ---
    "market.Instrument": ("bifrost_contracts.market.market_pb2", "Instrument"),
    "market.BookLevel": ("bifrost_contracts.market.market_pb2", "BookLevel"),
    "market.BookView": ("bifrost_contracts.market.market_pb2", "BookView"),
    "market.ImbalancePrint": ("bifrost_contracts.market.market_pb2", "ImbalancePrint"),

    # --- auction.proto (3) ---
    "auction.BidStep": ("bifrost_contracts.auction.auction_pb2", "BidStep"),
    "auction.BidMatrix": ("bifrost_contracts.auction.auction_pb2", "BidMatrix"),
    "auction.ClearingResult": ("bifrost_contracts.auction.auction_pb2", "ClearingResult"),

    # --- round.proto (1) ---
    "round.RoundState": ("bifrost_contracts.round.round_pb2", "RoundState"),

    # --- events.proto: Event wrapper (one row per oneof variant; all share
    #                   the same Python Event class) (6) + bare PhysicalShock (1) ---
    "events.Event.RegimeChange": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.Event.ForecastRevision": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.Event.News": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.Event.MarketAlert": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.Event.ConfigChange": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.Event.PhysicalShock": ("bifrost_contracts.events.events_pb2", "Event"),
    "events.PhysicalShock": ("bifrost_contracts.events.events_pb2", "PhysicalShock"),

    # --- strategy.proto: StrategyCommand oneof (5) ---
    "strategy.StrategyCommand.Register": ("bifrost_contracts.strategy.strategy_pb2", "StrategyCommand"),
    "strategy.StrategyCommand.OrderSubmit": ("bifrost_contracts.strategy.strategy_pb2", "StrategyCommand"),
    "strategy.StrategyCommand.OrderCancel": ("bifrost_contracts.strategy.strategy_pb2", "StrategyCommand"),
    "strategy.StrategyCommand.OrderReplace": ("bifrost_contracts.strategy.strategy_pb2", "StrategyCommand"),
    "strategy.StrategyCommand.BidMatrixSubmit": ("bifrost_contracts.strategy.strategy_pb2", "StrategyCommand"),

    # --- strategy.proto: MarketEvent oneof (12) ---
    "strategy.MarketEvent.RegisterAck": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.BookUpdate": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.Trade": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.ForecastUpdate": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.PublicEvent": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.OrderAck": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.OrderReject": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.Fill": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.RoundState": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.Scorecard": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.PositionSnapshot": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),
    "strategy.MarketEvent.ImbalancePrint": ("bifrost_contracts.strategy.strategy_pb2", "MarketEvent"),

    # --- mc.proto: McCommand oneof (21) ---
    "mc.McCommand.AuctionOpen": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.AuctionClose": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.RoundStart": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.RoundEnd": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.Gate": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.Settle": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.NextRound": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.Pause": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.Resume": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.Abort": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.ForecastRevise": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.RegimeForce": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.NewsFire": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.NewsPublish": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.AlertUrgent": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.PhysicalShock": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.TeamKick": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.TeamReset": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.ConfigSet": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.LeaderboardReveal": ("bifrost_contracts.mc.mc_pb2", "McCommand"),
    "mc.McCommand.EventEnd": ("bifrost_contracts.mc.mc_pb2", "McCommand"),

    # --- mc.proto: bare PhysicalShockCmd standalone (1) + McCommandResult standalone (1) ---
    "mc.PhysicalShockCmd": ("bifrost_contracts.mc.mc_pb2", "PhysicalShockCmd"),
    "mc.McCommandResult": ("bifrost_contracts.mc.mc_pb2", "McCommandResult"),
}


def main() -> int:
    ap = argparse.ArgumentParser(description="CONT-04 proto bytes round-trip harness")
    ap.add_argument("--in", dest="in_", required=True, help="canonical bytes input tempfile")
    ap.add_argument("--type", required=True, help="TYPE_MAP key (message TypeName)")
    ap.add_argument("--out", required=True, help="re-serialised bytes output tempfile")
    args = ap.parse_args()

    try:
        module_name, class_name = TYPE_MAP[args.type]
    except KeyError:
        print(
            f"Unknown --type: {args.type!r}. Add a row to TYPE_MAP + CanonicalBuilders.",
            file=sys.stderr,
        )
        return 1

    module = importlib.import_module(module_name)
    cls = getattr(module, class_name)

    # Pitfall E: ParseFromString + SerializeToString ONLY. Never attribute
    # assignment on nested oneof fields after parse — that path silently drifts
    # between protobuf 5.x pure-python and upb runtimes on edge cases (well-
    # known types, packed repeateds). Bytes in, bytes out, no field touches.
    msg = cls()
    msg.ParseFromString(Path(args.in_).read_bytes())
    Path(args.out).write_bytes(msg.SerializeToString())
    return 0


if __name__ == "__main__":
    sys.exit(main())
