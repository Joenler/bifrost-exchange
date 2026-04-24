-- Migrations/001_initial.sql
-- Initial BIFROST recorder schema. The schema_version table is managed
-- separately by SchemaMigrator (CREATE TABLE IF NOT EXISTS + INSERT on
-- successful apply); it is intentionally not defined here.
--
-- Write consumers in this milestone: book_updates, trades, orders, fills,
-- rejects, events. The mc_commands table is populated by the MC console
-- pipeline landing later. The scorecards table is populated by the scoring
-- exporter landing later. Both tables are created empty up front to keep
-- version 1 forward-compatible per D-12 (no mid-milestone migrations).

CREATE TABLE book_updates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    instrument_id TEXT NOT NULL,
    side TEXT NOT NULL,         -- 'Buy' | 'Sell'
    level INTEGER NOT NULL,     -- price level index within the side (0 = best)
    price_ticks INTEGER NOT NULL,
    quantity TEXT NOT NULL,     -- decimal as invariant-culture string (DecimalTypeHandler)
    count INTEGER NOT NULL,     -- order count at this level after the update
    sequence INTEGER NOT NULL   -- per-instrument monotonic
);
CREATE INDEX idx_book_updates_instrument_seq ON book_updates(instrument_id, sequence);
CREATE INDEX idx_book_updates_ts ON book_updates(ts_ns);

CREATE TABLE trades (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    instrument_id TEXT NOT NULL,
    trade_id INTEGER NOT NULL,
    price_ticks INTEGER NOT NULL,
    quantity TEXT NOT NULL,
    aggressor_side TEXT NOT NULL,
    sequence INTEGER NOT NULL
);
CREATE INDEX idx_trades_instrument_seq ON trades(instrument_id, sequence);
CREATE INDEX idx_trades_ts ON trades(ts_ns);

CREATE TABLE orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    client_id TEXT NOT NULL,
    instrument_id TEXT NOT NULL,
    order_id INTEGER NOT NULL,
    action TEXT NOT NULL,           -- 'submit' | 'cancel' | 'replace'
    side TEXT,                      -- NULL on cancel
    price_ticks INTEGER,            -- NULL for market orders and cancels
    quantity TEXT,                  -- NULL on cancel
    order_type TEXT,                -- 'Limit' | 'Market' | NULL on cancel
    correlation_id TEXT
);
CREATE INDEX idx_orders_client ON orders(client_id);
CREATE INDEX idx_orders_instrument ON orders(instrument_id);
CREATE INDEX idx_orders_ts ON orders(ts_ns);

CREATE TABLE fills (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    instrument_id TEXT NOT NULL,
    trade_id INTEGER NOT NULL,
    price_ticks INTEGER NOT NULL,
    quantity TEXT NOT NULL,
    aggressor_side TEXT NOT NULL,
    maker_client_id TEXT NOT NULL,
    taker_client_id TEXT NOT NULL,
    maker_order_id INTEGER NOT NULL,
    taker_order_id INTEGER NOT NULL
);
CREATE INDEX idx_fills_maker ON fills(maker_client_id);
CREATE INDEX idx_fills_taker ON fills(taker_client_id);
CREATE INDEX idx_fills_trade ON fills(trade_id);

CREATE TABLE rejects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    client_id TEXT NOT NULL,
    instrument_id TEXT,
    rejection_code TEXT NOT NULL,   -- matches RejectionCode enum name
    reason_detail TEXT,             -- D-11 sidecar
    correlation_id TEXT
);
CREATE INDEX idx_rejects_client ON rejects(client_id);
CREATE INDEX idx_rejects_ts ON rejects(ts_ns);

CREATE TABLE events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    kind TEXT NOT NULL,             -- 'news' | 'alert' | 'round_state' | 'physical_shock' | ...
    severity TEXT NOT NULL,         -- 'info' | 'urgent'
    payload_json TEXT NOT NULL
);
CREATE INDEX idx_events_kind ON events(kind);
CREATE INDEX idx_events_ts ON events(ts_ns);

-- mc_commands: written by the MC console recorder consumer when it lands.
CREATE TABLE mc_commands (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    command TEXT NOT NULL,
    args_json TEXT NOT NULL,
    result_json TEXT NOT NULL,
    operator_hostname TEXT NOT NULL
);
CREATE INDEX idx_mc_commands_ts ON mc_commands(ts_ns);

-- scorecards: written by the scoring exporter when it lands.
CREATE TABLE scorecards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    round_number INTEGER NOT NULL,
    client_id TEXT NOT NULL,
    trade_pnl TEXT NOT NULL,
    imbalance_pnl TEXT NOT NULL,
    fees TEXT NOT NULL,
    otr_penalty TEXT NOT NULL,
    total TEXT NOT NULL,
    UNIQUE(round_number, client_id)
);

-- imbalance_settlements: per-team per-quarter imbalance settlement rows (append at v1 — schema_version stays 1).
CREATE TABLE imbalance_settlements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ns INTEGER NOT NULL,
    round_number INTEGER NOT NULL,
    client_id TEXT NOT NULL,
    instrument_id TEXT NOT NULL,
    quarter_index INTEGER NOT NULL,
    position_ticks INTEGER NOT NULL,
    p_imb_ticks INTEGER NOT NULL,
    imbalance_pnl_ticks INTEGER NOT NULL,
    UNIQUE(round_number, client_id, quarter_index)
);
CREATE INDEX idx_imbalance_settlements_round_client ON imbalance_settlements(round_number, client_id);
CREATE INDEX idx_imbalance_settlements_ts ON imbalance_settlements(ts_ns);
