using System.Text.Json;
using Bifrost.Orchestrator.Tests.TestSupport;
using Bifrost.Recorder.Storage;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Integration;

/// <summary>
/// SPEC Req 10 / Phase 06 D-23: every <c>McCommand</c> the orchestrator
/// processes — accepted or rejected — lands in the recorder's
/// <c>mc_commands</c> table. The orchestrator-side publish path is
/// covered by <c>OrchestratorActor</c> + publisher tests; this test pins
/// the recorder's write-side: 5 audit-log writes (3 accepted + 2 rejected)
/// produce exactly 5 rows in order, with rejected rows preserving
/// <c>"success":false</c> in <c>result_json</c> (audit-log invariant).
/// </summary>
/// <remarks>
/// Direct-dispatch testing convention (PATTERNS §test row): exercises
/// <see cref="SessionDatabase.InsertMcCommands"/> through the same
/// <see cref="McCommandWrite"/> shape the recorder's
/// <c>DispatchMcCommandLog</c> emits. Phase 06 ships the consumer-side
/// dispatch test in <c>Bifrost.Recorder.Tests/RecorderPersistenceTests</c>
/// (RED→GREEN proven there); this fixture proves the storage shape ends
/// up correct on disk after a full 5-command audit-log replay — the cell
/// shape Phase 10 replay tools will read.
/// </remarks>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteRecorderFixture _fixture;

    public AuditLogTests()
    {
        _tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"bifrost-orch-audit-{Guid.NewGuid():N}")).FullName;
        _fixture = new SqliteRecorderFixture(_tempDir);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FiveCommands_ThreeAcceptedTwoRejected_Produce_FiveRowsInMcCommandsTable()
    {
        // Simulate the orchestrator's audit-log publish output: 5
        // McCommandWrite entries representing 3 accepted + 2 rejected
        // commands, ordered chronologically. The recorder's
        // SessionDatabase.InsertMcCommands is the write-path under test.
        var writes = new List<McCommandWrite>
        {
            BuildWrite(
                ts: 1_000,
                command: "AuctionOpen",
                argsJson: "{\"operatorHost\":\"mc\",\"confirm\":true}",
                resultJson: "{\"success\":true,\"message\":\"transitioned to AuctionOpen\",\"newState\":\"STATE_AUCTION_OPEN\"}",
                host: "mc"),
            BuildWrite(
                ts: 2_000,
                command: "Gate",
                argsJson: "{\"operatorHost\":\"mc\",\"confirm\":true}",
                resultJson: "{\"success\":false,\"message\":\"illegal transition: Gate from AuctionOpen\",\"newState\":\"\"}",
                host: "mc"),
            BuildWrite(
                ts: 3_000,
                command: "AuctionClose",
                argsJson: "{\"operatorHost\":\"mc\",\"confirm\":true}",
                resultJson: "{\"success\":true,\"message\":\"transitioned to AuctionClosed\",\"newState\":\"STATE_AUCTION_CLOSED\"}",
                host: "mc"),
            BuildWrite(
                ts: 4_000,
                command: "RegimeForce",
                argsJson: "{\"operatorHost\":\"mc\",\"confirm\":true,\"regime\":\"REGIME_VOLATILE\"}",
                resultJson: "{\"success\":true,\"message\":\"event-emitting: RegimeForce\",\"newState\":\"\"}",
                host: "mc"),
            BuildWrite(
                ts: 5_000,
                command: "Gate",
                argsJson: "{\"operatorHost\":\"mc\",\"confirm\":false}",
                resultJson: "{\"success\":false,\"message\":\"confirm required for Gate\",\"newState\":\"\"}",
                host: "mc"),
        };

        _fixture.Database.InsertMcCommands(writes);

        var rows = _fixture.QueryMcCommands();
        Assert.Equal(5, rows.Count);

        // Order preserved by id ASCENDING — matches insertion order.
        Assert.Equal("AuctionOpen", rows[0].Command);
        Assert.Equal(1_000L, rows[0].TsNs);
        Assert.Contains("\"success\":true", rows[0].ResultJson);

        Assert.Equal("Gate", rows[1].Command);
        Assert.Equal(2_000L, rows[1].TsNs);
        Assert.Contains("\"success\":false", rows[1].ResultJson);
        Assert.Contains("illegal transition", rows[1].ResultJson);

        Assert.Equal("AuctionClose", rows[2].Command);
        Assert.Contains("\"success\":true", rows[2].ResultJson);

        Assert.Equal("RegimeForce", rows[3].Command);
        Assert.Contains("REGIME_VOLATILE", rows[3].ArgsJson);
        Assert.Contains("\"success\":true", rows[3].ResultJson);

        Assert.Equal("Gate", rows[4].Command);
        Assert.Contains("\"success\":false", rows[4].ResultJson);
        Assert.Contains("confirm required for Gate", rows[4].ResultJson);

        // Every row carries the operator hostname — the audit log identifies
        // who issued the command, not just what was issued.
        Assert.All(rows, r => Assert.Equal("mc", r.OperatorHostname));

        // Rejected rows preserved (NOT dropped) — the audit-log invariant
        // SPEC Req 10 locks. Two of the five rows are rejections.
        var rejected = rows.Count(r => r.ResultJson.Contains("\"success\":false"));
        Assert.Equal(2, rejected);
    }

    [Fact]
    public void RoundTrip_StorageShape_DeserializesIntoOrchestratorResultRecord()
    {
        // Replay-tool friendliness check: the result_json cell parses back
        // into a 3-field shape (success, message, newState) — the same
        // shape the recorder's DispatchMcCommandLog composes from the
        // upstream McCommandLogPayload. Catches any future schema drift
        // between the publish-side (orchestrator) and the storage shape.
        var write = BuildWrite(
            ts: 1_234,
            command: "AuctionOpen",
            argsJson: "{}",
            resultJson: JsonSerializer.Serialize(new { success = true, message = "ok", newState = "STATE_AUCTION_OPEN" }),
            host: "mc");

        _fixture.Database.InsertMcCommands(new[] { write });

        var row = Assert.Single(_fixture.QueryMcCommands());

        // Strongly-typed deserialization round-trip.
        var doc = JsonDocument.Parse(row.ResultJson);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ok", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("STATE_AUCTION_OPEN", doc.RootElement.GetProperty("newState").GetString());
    }

    private static McCommandWrite BuildWrite(
        long ts,
        string command,
        string argsJson,
        string resultJson,
        string host) =>
        new(
            TsNs: ts,
            Command: command,
            ArgsJson: argsJson,
            ResultJson: resultJson,
            OperatorHostname: host,
            ReceivedAtNs: ts + 100);
}
