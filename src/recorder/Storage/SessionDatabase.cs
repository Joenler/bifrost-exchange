using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bifrost.Recorder.Storage;

/// <summary>
/// Single-writer SQLite session database for the BIFROST recorder. Owns one
/// open <see cref="SqliteConnection"/>, applies WAL-mode pragmas, and exposes
/// per-table bulk inserts behind Arena's prepared-command reuse pattern.
/// </summary>
/// <remarks>
/// Schema creation is NOT performed here: <see cref="Infrastructure.SchemaMigrator"/>
/// owns that, driven off embedded <c>Migrations/*.sql</c> resources. This class
/// applies the per-connection pragmas in <see cref="InitializePragmas"/> and
/// the caller wires the migrator in immediately after opening the connection
/// but before registering the write loop as a hosted service.
///
/// The public surface (Connection, InitializePragmas, Execute, ExecuteBatch,
/// Query, BeginTransaction) is a superset of the Plan 08 shim it replaces;
/// <see cref="SchemaMigrator"/> continues to depend on exactly those members
/// without change.
/// </remarks>
public sealed class SessionDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SessionDatabase(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    private SessionDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static SessionDatabase OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        return new SessionDatabase(connection);
    }

    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Apply the 5-pragma WAL sequence. Per-connection state, not schema
    /// state — migrations (<c>Migrations/*.sql</c>) own the table DDL.
    /// </summary>
    public void InitializePragmas()
    {
        ExecuteRaw("PRAGMA journal_mode = 'wal'");
        ExecuteRaw("PRAGMA synchronous = NORMAL");
        ExecuteRaw("PRAGMA wal_autocheckpoint = 10000");
        ExecuteRaw("PRAGMA cache_size = -32768");
        ExecuteRaw("PRAGMA mmap_size = 67108864");
    }

    /// <summary>
    /// Upper bound on rows returned by any single read-path call.
    /// Preserved from Arena; export/replay surfaces can enforce bounded reads.
    /// </summary>
    public const int MaxReadRows = 10_000;

    /// <summary>
    /// Thin parameterised Execute (Dapper). Preserved for <see cref="SchemaMigrator"/>.
    /// </summary>
    public void Execute(string sql, object? param = null) => _connection.Execute(sql, param);

    /// <summary>
    /// Multi-statement batch execute. Preserved for <see cref="SchemaMigrator"/>.
    /// </summary>
    public void ExecuteBatch(string sql) => _connection.Execute(sql);

    /// <summary>
    /// Thin parameterised Query (Dapper). Preserved for <see cref="SchemaMigrator"/>.
    /// </summary>
    public IEnumerable<T> Query<T>(string sql, object? param = null) => _connection.Query<T>(sql, param);

    /// <summary>
    /// Begin a transaction on the owned connection. Preserved for <see cref="SchemaMigrator"/>.
    /// </summary>
    public IDbTransaction BeginTransaction() => _connection.BeginTransaction();

    public void InsertBookUpdates(IReadOnlyList<BookUpdateWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_updates (
                ts_ns, instrument_id, side, level, price_ticks, quantity, count, sequence
            ) VALUES (
                $ts_ns, $instrument_id, $side, $level, $price_ticks, $quantity, $count, $sequence
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pSide = cmd.Parameters.Add("$side", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("$level", SqliteType.Integer);
        var pPrice = cmd.Parameters.Add("$price_ticks", SqliteType.Integer);
        var pQuantity = cmd.Parameters.Add("$quantity", SqliteType.Text);
        var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
        var pSequence = cmd.Parameters.Add("$sequence", SqliteType.Integer);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pInstrument.Value = w.InstrumentId;
            pSide.Value = w.Side;
            pLevel.Value = w.Level;
            pPrice.Value = w.PriceTicks;
            pQuantity.Value = Inv(w.Quantity);
            pCount.Value = w.Count;
            pSequence.Value = w.Sequence;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertTrades(IReadOnlyList<TradeWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO trades (
                ts_ns, instrument_id, trade_id, price_ticks, quantity, aggressor_side, sequence
            ) VALUES (
                $ts_ns, $instrument_id, $trade_id, $price_ticks, $quantity, $aggressor_side, $sequence
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pTradeId = cmd.Parameters.Add("$trade_id", SqliteType.Integer);
        var pPrice = cmd.Parameters.Add("$price_ticks", SqliteType.Integer);
        var pQuantity = cmd.Parameters.Add("$quantity", SqliteType.Text);
        var pAggressor = cmd.Parameters.Add("$aggressor_side", SqliteType.Text);
        var pSequence = cmd.Parameters.Add("$sequence", SqliteType.Integer);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pInstrument.Value = w.InstrumentId;
            pTradeId.Value = w.TradeId;
            pPrice.Value = w.PriceTicks;
            pQuantity.Value = Inv(w.Quantity);
            pAggressor.Value = w.AggressorSide;
            pSequence.Value = w.Sequence;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertOrders(IReadOnlyList<OrderWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO orders (
                ts_ns, client_id, instrument_id, order_id, action,
                side, price_ticks, quantity, order_type, correlation_id
            ) VALUES (
                $ts_ns, $client_id, $instrument_id, $order_id, $action,
                $side, $price_ticks, $quantity, $order_type, $correlation_id
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pClient = cmd.Parameters.Add("$client_id", SqliteType.Text);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pOrderId = cmd.Parameters.Add("$order_id", SqliteType.Integer);
        var pAction = cmd.Parameters.Add("$action", SqliteType.Text);
        var pSide = cmd.Parameters.Add("$side", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("$price_ticks", SqliteType.Integer);
        var pQuantity = cmd.Parameters.Add("$quantity", SqliteType.Text);
        var pOrderType = cmd.Parameters.Add("$order_type", SqliteType.Text);
        var pCorrelation = cmd.Parameters.Add("$correlation_id", SqliteType.Text);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pClient.Value = w.ClientId;
            pInstrument.Value = w.InstrumentId;
            pOrderId.Value = w.OrderId;
            pAction.Value = w.Action;
            pSide.Value = (object?)w.Side ?? DBNull.Value;
            pPrice.Value = w.PriceTicks.HasValue ? (object)w.PriceTicks.Value : DBNull.Value;
            pQuantity.Value = w.Quantity.HasValue ? (object)Inv(w.Quantity.Value) : DBNull.Value;
            pOrderType.Value = (object?)w.OrderType ?? DBNull.Value;
            pCorrelation.Value = (object?)w.CorrelationId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertFills(IReadOnlyList<FillWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fills (
                ts_ns, instrument_id, trade_id, price_ticks, quantity, aggressor_side,
                maker_client_id, taker_client_id, maker_order_id, taker_order_id
            ) VALUES (
                $ts_ns, $instrument_id, $trade_id, $price_ticks, $quantity, $aggressor_side,
                $maker_client_id, $taker_client_id, $maker_order_id, $taker_order_id
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pTradeId = cmd.Parameters.Add("$trade_id", SqliteType.Integer);
        var pPrice = cmd.Parameters.Add("$price_ticks", SqliteType.Integer);
        var pQuantity = cmd.Parameters.Add("$quantity", SqliteType.Text);
        var pAggressor = cmd.Parameters.Add("$aggressor_side", SqliteType.Text);
        var pMakerClient = cmd.Parameters.Add("$maker_client_id", SqliteType.Text);
        var pTakerClient = cmd.Parameters.Add("$taker_client_id", SqliteType.Text);
        var pMakerOrder = cmd.Parameters.Add("$maker_order_id", SqliteType.Integer);
        var pTakerOrder = cmd.Parameters.Add("$taker_order_id", SqliteType.Integer);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pInstrument.Value = w.InstrumentId;
            pTradeId.Value = w.TradeId;
            pPrice.Value = w.PriceTicks;
            pQuantity.Value = Inv(w.Quantity);
            pAggressor.Value = w.AggressorSide;
            pMakerClient.Value = w.MakerClientId;
            pTakerClient.Value = w.TakerClientId;
            pMakerOrder.Value = w.MakerOrderId;
            pTakerOrder.Value = w.TakerOrderId;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertRejects(IReadOnlyList<RejectWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rejects (
                ts_ns, client_id, instrument_id, rejection_code, reason_detail, correlation_id
            ) VALUES (
                $ts_ns, $client_id, $instrument_id, $rejection_code, $reason_detail, $correlation_id
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pClient = cmd.Parameters.Add("$client_id", SqliteType.Text);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pCode = cmd.Parameters.Add("$rejection_code", SqliteType.Text);
        var pDetail = cmd.Parameters.Add("$reason_detail", SqliteType.Text);
        var pCorrelation = cmd.Parameters.Add("$correlation_id", SqliteType.Text);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pClient.Value = w.ClientId;
            pInstrument.Value = (object?)w.InstrumentId ?? DBNull.Value;
            pCode.Value = w.RejectionCode;
            pDetail.Value = (object?)w.ReasonDetail ?? DBNull.Value;
            pCorrelation.Value = (object?)w.CorrelationId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertEvents(IReadOnlyList<EventWrite> batch)
    {
        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events (
                ts_ns, kind, severity, payload_json
            ) VALUES (
                $ts_ns, $kind, $severity, $payload_json
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pSeverity = cmd.Parameters.Add("$severity", SqliteType.Text);
        var pPayload = cmd.Parameters.Add("$payload_json", SqliteType.Text);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pKind.Value = w.Kind;
            pSeverity.Value = w.Severity;
            pPayload.Value = w.PayloadJson;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Bulk-insert MC command audit rows into the Phase 02-shipped
    /// <c>mc_commands</c> table. Mirrors the <see cref="InsertEvents"/>
    /// prepared-statement shape from PATTERNS §H — single transaction, single
    /// reused command, parameter values reset per row.
    /// </summary>
    /// <remarks>
    /// Phase 06 D-23: the orchestrator publishes one envelope per processed
    /// <c>McCommand</c> on <c>bifrost.mc.v1/mc.command.{cmd_snake}</c>; the
    /// recorder consumes via <see cref="Infrastructure.RabbitMqRecorderConsumer"/>
    /// and routes here. Schema unchanged from
    /// <c>Migrations/001_initial.sql</c> — D-12 zero-migrations posture.
    /// </remarks>
    public void InsertMcCommands(IReadOnlyList<McCommandWrite> batch)
    {
        if (batch.Count == 0) return;

        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mc_commands (
                ts_ns, command, args_json, result_json, operator_hostname
            ) VALUES (
                $ts_ns, $command, $args_json, $result_json, $operator_hostname
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pCommand = cmd.Parameters.Add("$command", SqliteType.Text);
        var pArgs = cmd.Parameters.Add("$args_json", SqliteType.Text);
        var pResult = cmd.Parameters.Add("$result_json", SqliteType.Text);
        var pHost = cmd.Parameters.Add("$operator_hostname", SqliteType.Text);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pCommand.Value = w.Command;
            pArgs.Value = w.ArgsJson;
            pResult.Value = w.ResultJson;
            pHost.Value = w.OperatorHostname;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertImbalanceSettlements(IReadOnlyList<ImbalanceSettlementWrite> batch)
    {
        if (batch.Count == 0) return;

        using var transaction = _connection.BeginTransaction();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imbalance_settlements (
                ts_ns, round_number, client_id, instrument_id, quarter_index,
                position_ticks, p_imb_ticks, imbalance_pnl_ticks
            ) VALUES (
                $ts_ns, $round_number, $client_id, $instrument_id, $quarter_index,
                $position_ticks, $p_imb_ticks, $imbalance_pnl_ticks
            )
            """;

        var pTs = cmd.Parameters.Add("$ts_ns", SqliteType.Integer);
        var pRound = cmd.Parameters.Add("$round_number", SqliteType.Integer);
        var pClient = cmd.Parameters.Add("$client_id", SqliteType.Text);
        var pInstrument = cmd.Parameters.Add("$instrument_id", SqliteType.Text);
        var pQh = cmd.Parameters.Add("$quarter_index", SqliteType.Integer);
        var pPos = cmd.Parameters.Add("$position_ticks", SqliteType.Integer);
        var pPimb = cmd.Parameters.Add("$p_imb_ticks", SqliteType.Integer);
        var pPnl = cmd.Parameters.Add("$imbalance_pnl_ticks", SqliteType.Integer);

        cmd.Prepare();

        foreach (var w in batch)
        {
            pTs.Value = w.TsNs;
            pRound.Value = w.RoundNumber;
            pClient.Value = w.ClientId;
            pInstrument.Value = w.InstrumentId;
            pQh.Value = w.QuarterIndex;
            pPos.Value = w.PositionTicks;
            pPimb.Value = w.PImbTicks;
            pPnl.Value = w.ImbalancePnlTicks;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Per-table row counts used by the shutdown hook to stamp the manifest.
    /// </summary>
    public RecorderEventCounts GetEventCounts()
    {
        return new RecorderEventCounts(
            BookUpdates: CountTable("book_updates"),
            Trades: CountTable("trades"),
            Orders: CountTable("orders"),
            Fills: CountTable("fills"),
            Rejects: CountTable("rejects"),
            Events: CountTable("events"));
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private int CountTable(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    internal object? QueryPragma(string pragma)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}";
        return cmd.ExecuteScalar();
    }

    private void ExecuteRaw(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Inv(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Table row counts snapshot. Fields map 1:1 to BIFROST schema tables.
/// </summary>
public sealed record RecorderEventCounts(
    int BookUpdates,
    int Trades,
    int Orders,
    int Fills,
    int Rejects,
    int Events);
