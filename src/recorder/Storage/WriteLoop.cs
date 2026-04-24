using System.Diagnostics;
using System.Threading.Channels;
using Bifrost.Recorder.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Recorder.Storage;

/// <summary>
/// Single-reader drain loop from the in-process <see cref="Channel{T}"/> into
/// <see cref="SessionDatabase"/>. Preserves Arena's adaptive batch sizing
/// (MinBatchSize=100, MaxBatchSizeCap=1000, ScaleUp=0.7, ScaleDown=0.3,
/// FlushInterval=250ms) and drain-on-stop behaviour.
/// </summary>
public sealed class WriteLoop : BackgroundService
{
    private readonly Channel<WriteCommand> _channel;
    private readonly SessionDatabase _db;
    private readonly RecorderMetrics _metrics;
    private readonly ILogger<WriteLoop> _logger;
    private readonly int _channelCapacity;

    private const int MinBatchSize = 100;
    private const int MaxBatchSizeCap = 1000;
    private const double ScaleUpThreshold = 0.7;
    private const double ScaleDownThreshold = 0.3;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private int _currentMaxBatchSize = MinBatchSize;
    private BackpressureLevel _lastLevel = BackpressureLevel.Normal;
    private double _lastBatchDurationMs;

    internal int CurrentMaxBatchSize => _currentMaxBatchSize;
    public double LastBatchDurationMs => _lastBatchDurationMs;

    public WriteLoop(
        Channel<WriteCommand> channel,
        SessionDatabase db,
        RecorderMetrics metrics,
        ILogger<WriteLoop> logger,
        int channelCapacity = 10_000)
    {
        _channel = channel;
        _db = db;
        _metrics = metrics;
        _logger = logger;
        _channelCapacity = channelCapacity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<WriteCommand>();
        var reader = _channel.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(FlushInterval);

                try
                {
                    var item = await reader.ReadAsync(cts.Token);
                    batch.Add(item);

                    while (batch.Count < _currentMaxBatchSize && reader.TryRead(out var next))
                        batch.Add(next);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
                catch (ChannelClosedException)
                {
                    // Consumer.StopAsync may complete the writer before this loop
                    // observes stoppingToken cancellation (ordering depends on
                    // Host shutdown service order). Treat as a clean shutdown
                    // signal: drain what remains and exit.
                    CheckBackpressure();
                    AdjustBatchSize();
                    if (batch.Count > 0)
                    {
                        FlushBatch(batch);
                        batch.Clear();
                    }
                    DrainRemaining(batch);
                    return;
                }

                CheckBackpressure();
                AdjustBatchSize();

                if (batch.Count > 0)
                {
                    FlushBatch(batch);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        DrainRemaining(batch);
    }

    private void DrainRemaining(List<WriteCommand> batch)
    {
        while (_channel.Reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
        {
            FlushBatch(batch);
            batch.Clear();
        }
    }

    internal void CheckBackpressure()
    {
        var depth = _channel.Reader.Count;
        var ratio = (double)depth / _channelCapacity;

        _metrics.ChannelDepth = depth;

        var newLevel = ratio switch
        {
            >= 0.9 => BackpressureLevel.Critical,
            >= 0.7 => BackpressureLevel.Warning,
            _ => BackpressureLevel.Normal,
        };

        if (newLevel != _lastLevel)
        {
            switch (newLevel)
            {
                case BackpressureLevel.Critical:
                    _metrics.BackpressureWarnings++;
                    _logger.LogWarning(
                        "Channel backpressure CRITICAL: {Depth}/{Capacity} ({Ratio:P0})",
                        depth, _channelCapacity, ratio);
                    break;

                case BackpressureLevel.Warning:
                    _metrics.BackpressureWarnings++;
                    _logger.LogWarning(
                        "Channel backpressure WARNING: {Depth}/{Capacity} ({Ratio:P0})",
                        depth, _channelCapacity, ratio);
                    break;
            }

            _lastLevel = newLevel;
        }
    }

    internal void AdjustBatchSize()
    {
        var ratio = (double)_channel.Reader.Count / _channelCapacity;

        if (ratio > ScaleUpThreshold && _currentMaxBatchSize < MaxBatchSizeCap)
            _currentMaxBatchSize = Math.Min(_currentMaxBatchSize * 2, MaxBatchSizeCap);
        else if (ratio < ScaleDownThreshold && _currentMaxBatchSize > MinBatchSize)
            _currentMaxBatchSize = Math.Max(_currentMaxBatchSize / 2, MinBatchSize);
    }

    private void FlushBatch(List<WriteCommand> batch)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        var bookUpdates = new List<BookUpdateWrite>();
        var trades = new List<TradeWrite>();
        var orders = new List<OrderWrite>();
        var fills = new List<FillWrite>();
        var rejects = new List<RejectWrite>();
        var events = new List<EventWrite>();
        var imbalanceSettlements = new List<ImbalanceSettlementWrite>();

        foreach (var cmd in batch)
        {
            switch (cmd)
            {
                case BookUpdateWrite bu:
                    bookUpdates.Add(bu);
                    break;
                case TradeWrite tr:
                    trades.Add(tr);
                    break;
                case OrderWrite or:
                    orders.Add(or);
                    break;
                case FillWrite fi:
                    fills.Add(fi);
                    break;
                case RejectWrite rj:
                    rejects.Add(rj);
                    break;
                case EventWrite ev:
                    events.Add(ev);
                    break;
                case ImbalanceSettlementWrite imb:
                    imbalanceSettlements.Add(imb);
                    break;
            }
        }

        if (bookUpdates.Count > 0)
        {
            _db.InsertBookUpdates(bookUpdates);
            _logger.LogDebug("Flushed {Count} book updates", bookUpdates.Count);
        }

        if (trades.Count > 0)
        {
            _db.InsertTrades(trades);
            _logger.LogDebug("Flushed {Count} trades", trades.Count);
        }

        if (orders.Count > 0)
        {
            _db.InsertOrders(orders);
            _logger.LogDebug("Flushed {Count} orders", orders.Count);
        }

        if (fills.Count > 0)
        {
            _db.InsertFills(fills);
            _logger.LogDebug("Flushed {Count} fills", fills.Count);
        }

        if (rejects.Count > 0)
        {
            _db.InsertRejects(rejects);
            _logger.LogDebug("Flushed {Count} rejects", rejects.Count);
        }

        if (events.Count > 0)
        {
            _db.InsertEvents(events);
            _logger.LogDebug("Flushed {Count} events", events.Count);
        }

        if (imbalanceSettlements.Count > 0)
        {
            _db.InsertImbalanceSettlements(imbalanceSettlements);
            _logger.LogDebug("Flushed {Count} imbalance settlements", imbalanceSettlements.Count);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _metrics.LastBatchDurationMs = elapsedMs;
        _metrics.EventsRecorded += batch.Count;
        _lastBatchDurationMs = elapsedMs;
    }

    private enum BackpressureLevel
    {
        Normal,
        Warning,
        Critical,
    }
}
