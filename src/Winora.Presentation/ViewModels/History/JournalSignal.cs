// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>
/// Says that a change was written to a journal (an apply or an undo). Pages that read their state once read it again on their next activation when the version moved since their last read,
/// and the settings model reads the user settings again when another area wrote them (a profile run or an undo on the History page). It carries no data and never polls.
/// </summary>
public sealed class JournalSignal
{
    private long version;

    public long Version => Interlocked.Read(ref version);

    /// <summary>Raised on the thread that wrote, after the version moved. The argument is the area that wrote.</summary>
    public event Action<HistoryArea>? Changed;

    public void Raise(HistoryArea area)
    {
        Interlocked.Increment(ref version);
        Changed?.Invoke(area);
    }
}

/// <summary>What a reader remembers of the signal: the version it read last.</summary>
public sealed class JournalReader
{
    private long seen;
    private JournalSignal? signal;

    public JournalSignal? Signal { get => signal; set { signal = value; seen = value?.Version ?? 0; } }

    /// <summary>Something wrote since the last <see cref="MarkRead"/>.</summary>
    public bool IsStale => signal is not null && signal.Version != seen;

    /// <summary>Call when a read starts: a change during the read leaves the reader stale.</summary>
    public void MarkRead() => seen = signal?.Version ?? 0;

    /// <summary>This reader wrote: the others become stale, this one reads again by itself.</summary>
    public void Wrote(HistoryArea area) { signal?.Raise(area); MarkRead(); }
}
