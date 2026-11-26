namespace Sysacta.Mcp;

/// <summary>
/// Fixed one-minute window counting acquisitions per key and in total (SEC-05). Never waits and owns no timer: the
/// window rolls over lazily on the next call, which also drops every key of the previous window.
/// </summary>
/// <remarks>
/// A key is created only when an acquisition succeeds, which also counts against <paramref name="total"/>, so at most
/// <paramref name="total"/> keys exist at any time. Requests refused for a full key or total do not consume anything,
/// so one flooding address cannot use up the shared total beyond its own <paramref name="perKey"/> share.
/// </remarks>
internal sealed class RequestBudget(int perKey, int total, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _total;

    /// <summary>Keys tracked in the current window (tests).</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_lock)
            {
                Roll(time.GetUtcNow());
                return _counts.Count;
            }
        }
    }

    /// <summary>Counts one request for <paramref name="key"/>. On refusal, <paramref name="retryAfter"/> is the rest of the window.</summary>
    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            // Read the clock inside the lock: a time read earlier by another thread must not look like a clock going back.
            var now = time.GetUtcNow();
            Roll(now);
            var used = _counts.GetValueOrDefault(key);
            if (used >= perKey || _total >= total)
            {
                retryAfter = _windowStart + Window - now;
                return false;
            }

            _counts[key] = used + 1;
            _total++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private void Roll(DateTimeOffset now)
    {
        if (now >= _windowStart + Window || now < _windowStart)
        {
            _windowStart = now;
            _total = 0;
            _counts.Clear();
            _counts.TrimExcess(); // a burst of many keys does not keep its memory for the life of the host
        }
    }
}
