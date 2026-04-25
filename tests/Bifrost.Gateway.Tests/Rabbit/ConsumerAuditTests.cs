using Xunit;

namespace Bifrost.Gateway.Tests.Rabbit;

/// <summary>
/// Source-level audit tests on the 4 RabbitMQ consumers. The Pitfalls 6, 9,
/// 10 from 07-RESEARCH.md are easier to enforce by grepping the source than
/// by spinning up RabbitMQ for every CI run. These tests catch regressions
/// where someone (a) re-introduces a BasicGetAsync poll, (b) shares a
/// channel across consumers, (c) writes to <c>Outbound</c> while still
/// holding <c>StateLock</c>.
/// </summary>
public class ConsumerAuditTests
{
    /// <summary>
    /// Resolve the consumer source-file path from the test runtime base directory.
    /// Path layout:  tests/Bifrost.Gateway.Tests/bin/Release/net10.0/  →  ../../../..
    /// to repo root, then /src/gateway/Rabbit.
    /// </summary>
    private static string ResolveConsumerPath(string fileName)
    {
        // bin/Release/net10.0 -> tests/Bifrost.Gateway.Tests -> tests -> repo
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "src", "gateway", "Rabbit", fileName));
        if (File.Exists(candidate)) return candidate;
        // Fall-back: walk up the tree looking for the file (handles slightly
        // different bin layouts on CI vs dev).
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName, "src", "gateway", "Rabbit", fileName);
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate consumer source {fileName}", fileName);
    }

    /// <summary>
    /// Strip line comments (// ...) and block / XML doc comments so the audit
    /// asserts run on real code only. Preserves string literals.
    /// Approach is line-based: a line that starts with whitespace + "//" or
    /// whitespace + "/*" or whitespace + "*" is dropped; in-line trailing
    /// comments are also stripped.
    /// </summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        var inBlock = false;
        foreach (var rawLine in src.Split('\n'))
        {
            var line = rawLine;
            // Block-comment continuation handling.
            if (inBlock)
            {
                var endIdx = line.IndexOf("*/", StringComparison.Ordinal);
                if (endIdx < 0) continue;          // entirely inside a block comment
                line = line.Substring(endIdx + 2);  // resume past the close
                inBlock = false;
            }
            // Strip block comments that open and close on this line, possibly multiple.
            while (true)
            {
                var openIdx = line.IndexOf("/*", StringComparison.Ordinal);
                if (openIdx < 0) break;
                var closeIdx = line.IndexOf("*/", openIdx + 2, StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    line = line.Substring(0, openIdx);
                    inBlock = true;
                    break;
                }
                line = line.Substring(0, openIdx) + line.Substring(closeIdx + 2);
            }
            // Strip line comments. Naive: any "//" not inside a string. We do
            // not honour string literals here because the consumer source files
            // do not have "//" inside string literals.
            var lineCommentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (lineCommentIdx >= 0) line = line.Substring(0, lineCommentIdx);
            // Strip XML-doc lines that begin with "///"  (already covered by "//"
            // strip above, but keep this as a defensive mention).
            sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string ReadCode(string fileName) =>
        StripComments(File.ReadAllText(ResolveConsumerPath(fileName)));

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void Pitfall_9_NoBasicGetAsyncPoll(string fileName)
    {
        var src = ReadCode(fileName);
        Assert.DoesNotContain("BasicGetAsync", src);
    }

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void Pitfall_6_DedicatedChannelPerConsumer(string fileName)
    {
        var src = ReadCode(fileName);
        Assert.Contains("CreateChannelAsync", src);
        Assert.Contains("AsyncEventingBasicConsumer", src);
    }

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void NoConcurrentDictionary(string fileName)
    {
        var src = ReadCode(fileName);
        Assert.DoesNotContain("ConcurrentDictionary", src);
    }

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void NoBannedClockOrRandom(string fileName)
    {
        var src = ReadCode(fileName);
        Assert.DoesNotContain("DateTime.UtcNow", src);
        Assert.DoesNotContain("Random.Shared", src);
    }

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void Pitfall_10_NoOutboundWriteInsideStateLock(string fileName)
    {
        // Heuristic: scan each consumer's source for any sequence
        //   lock (...StateLock) { ... WriteAsync ... }
        // Catches the regression where someone moves the channel write inside
        // the state-lock scope. False positives are tolerated; false negatives
        // are not.
        var src = ReadCode(fileName);

        var idx = 0;
        var blockCount = 0;
        while (true)
        {
            // Match either "lock (state.StateLock)" or "lock (teamState.StateLock)" etc.
            var lockStart = src.IndexOf(".StateLock)", idx, StringComparison.Ordinal);
            if (lockStart < 0) break;
            var lockOpen = src.IndexOf('{', lockStart);
            if (lockOpen < 0) break;
            var lockClose = FindMatchingBrace(src, lockOpen);
            if (lockClose < 0) break;
            var body = src.Substring(lockOpen, lockClose - lockOpen);
            // The forbidden patterns: any *Outbound* write while inside the lock.
            Assert.DoesNotContain("Outbound.WriteAsync", body);
            Assert.DoesNotContain("Outbound.Writer.WriteAsync", body);
            // Also prohibit any direct `writer.WriteAsync` if we ever hold a local alias.
            Assert.DoesNotContain("writer.WriteAsync", body);
            blockCount++;
            idx = lockClose;
        }
        // Every consumer is expected to have at least one StateLock block (ring-Append).
        Assert.True(blockCount >= 1, $"{fileName} has no StateLock block — consumer must ring-Append under state lock.");
    }

    [Theory]
    [InlineData("PrivateEventConsumer.cs")]
    [InlineData("PublicEventConsumer.cs")]
    [InlineData("AuctionResultConsumer.cs")]
    [InlineData("RoundStateConsumer.cs")]
    public void RingAppendUnderStateLock(string fileName)
    {
        // Every consumer must Append to the team's ring, AND that Append must
        // happen under a StateLock block. We check by scanning each lock body
        // for "Ring.Append".
        var src = ReadCode(fileName);

        var idx = 0;
        var found = false;
        while (true)
        {
            var lockStart = src.IndexOf(".StateLock)", idx, StringComparison.Ordinal);
            if (lockStart < 0) break;
            var lockOpen = src.IndexOf('{', lockStart);
            if (lockOpen < 0) break;
            var lockClose = FindMatchingBrace(src, lockOpen);
            if (lockClose < 0) break;
            var body = src.Substring(lockOpen, lockClose - lockOpen);
            if (body.Contains("Ring.Append", StringComparison.Ordinal)) found = true;
            idx = lockClose;
        }
        Assert.True(found, $"{fileName} must call Ring.Append inside a StateLock block.");
    }

    [Fact]
    public void PrivateEventConsumer_RecordsTradeForOtrDenominator()
    {
        var src = ReadCode("PrivateEventConsumer.cs");
        // OTR denominator must stay current — Plan 04 OtrGuard.RecordTrade hook.
        Assert.Contains("OtrGuard.RecordTrade", src);
    }

    [Fact]
    public void RoundStateConsumer_WiresSettledToIterationOpenWipe()
    {
        var src = ReadCode("RoundStateConsumer.cs");
        // D-11 wipe wired.
        Assert.Contains("OnSettledToIterationOpen", src);
    }

    [Fact]
    public void AuctionResultConsumer_FiltersTeamNameNullVsNonNull()
    {
        var src = ReadCode("AuctionResultConsumer.cs");
        // Phase 05 D-09 fan-out predicate.
        Assert.True(
            src.Contains("clearing.TeamName") || src.Contains(".TeamName"),
            "AuctionResultConsumer must inspect ClearingResultDto.TeamName");
        // Both branches present — broadcast (SnapshotAll) and per-team (TryGetByName).
        Assert.Contains("SnapshotAll", src);
        Assert.Contains("TryGetByName", src);
    }

    private static int FindMatchingBrace(string src, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
