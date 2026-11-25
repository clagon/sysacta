namespace Sysacta.Core.Tools;

/// <summary>
/// Per-resource mutual exclusion (spec §10). Keys are taken in ordinal order to avoid deadlocks when a call needs
/// several resources. Entries are removed when unused.
/// </summary>
internal sealed class ResourceLocks
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<Lease> AcquireAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var held = new List<Entry>(ordered.Length);
        try
        {
            foreach (var key in ordered)
            {
                var entry = Rent(key);
                try
                {
                    await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Return(entry);
                    throw;
                }

                held.Add(entry);
            }
        }
        catch
        {
            Release(held);
            throw;
        }

        return new Lease(this, held);
    }

    /// <summary>Number of keys currently tracked (held or waited on). For tests.</summary>
    internal int TrackedCount
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    private Entry Rent(string key)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key);
                _entries.Add(key, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void Return(Entry entry)
    {
        lock (_entries)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(entry.Key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private void Release(List<Entry> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].Semaphore.Release();
            Return(held[i]);
        }
    }

    internal sealed class Lease(ResourceLocks owner, List<Entry> held) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(held);
            }
        }
    }

    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }
}
