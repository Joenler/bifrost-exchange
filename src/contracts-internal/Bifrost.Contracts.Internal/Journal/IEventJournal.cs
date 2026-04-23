namespace Bifrost.Contracts.Internal.Journal;

/// <summary>
/// Append-only journal for serialized command entries.
/// <para>Contract guarantees:</para>
/// <list type="number">
///   <item>Entries are durably stored before <see cref="AppendAsync"/> returns.</item>
///   <item><see cref="ReadAsync"/> returns entries in <see cref="JournalEntry.SequenceNumber"/>
///   order (monotonically increasing).</item>
///   <item>Appending an entry with a duplicate <see cref="JournalEntry.SequenceNumber"/>
///   is idempotent (no-op, not an error).</item>
/// </list>
/// </summary>
public interface IEventJournal
{
    /// <summary>
    /// Appends a single journal entry. Returns after the entry is durably stored.
    /// Duplicate sequence numbers are silently ignored.
    /// </summary>
    /// <param name="entry">The journal entry to append.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams journal entries with sequence numbers greater than <paramref name="afterSequence"/>,
    /// ordered by <see cref="JournalEntry.SequenceNumber"/> ascending.
    /// Pass 0 to read from the beginning.
    /// </summary>
    /// <param name="afterSequence">Return only entries with sequence numbers greater than this value. Defaults to 0 (read all).</param>
    /// <param name="cancellationToken">Cancellation token for the async enumeration.</param>
    IAsyncEnumerable<JournalEntry> ReadAsync(long afterSequence = 0, CancellationToken cancellationToken = default);
}
