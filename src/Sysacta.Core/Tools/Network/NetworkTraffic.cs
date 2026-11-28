using System.ComponentModel;

namespace Sysacta.Core.Tools.Network;

/// <summary>Why a rate is null for one adapter. Each refers to the sampling window of this request only.</summary>
public enum NetworkRateUnavailable
{
    /// <summary>The adapter was not in the second sample.</summary>
    AdapterRemoved,
    /// <summary>The adapter appeared during the window (no first sample).</summary>
    AdapterAdded,
    /// <summary>LUID or interface index changed: possibly a different interface object with the same id.</summary>
    IdentityChanged,
    /// <summary>Administrative, operational or media state changed during the window (a counter reset cannot be ruled out).</summary>
    StateChanged,
    /// <summary>A cumulative counter decreased (reset or wrap); no wrap correction is applied.</summary>
    CounterDecreased,
    /// <summary>The second interface table read failed.</summary>
    SampleFailed,
    /// <summary>The monotonic elapsed time was not usable.</summary>
    InvalidElapsed,
    /// <summary>Sysacta changed an adapter's administrative state during the window.</summary>
    ConfigurationChanged,
    /// <summary>The computed rate does not fit a 64-bit integer.</summary>
    InvalidData,
}

public sealed record NetworkTrafficEntry(
    string AdapterId,
    string? Name,
    NetworkAdapterKind Kind,
    NetworkOperState OperState,
    [property: Description("OS cumulative received bytes (64-bit counter) at the end of the window. JSON clients that use IEEE doubles lose precision above 2^53.")] ulong ReceivedBytesTotal,
    [property: Description("OS cumulative sent bytes (64-bit counter) at the end of the window.")] ulong SentBytesTotal,
    [property: Description("Bytes received during the window; null with rate_unavailable_reason.")] ulong? ReceivedBytesDelta,
    [property: Description("Bytes sent during the window; null with rate_unavailable_reason.")] ulong? SentBytesDelta,
    [property: Description("Received bits per second over the window: floor(delta_bytes * 8 / elapsed_seconds). 0 is a real measurement.")] long? ReceivedBitsPerSecond,
    [property: Description("Sent bits per second over the window: floor(delta_bytes * 8 / elapsed_seconds).")] long? SentBitsPerSecond,
    NetworkRateUnavailable? RateUnavailableReason);

public sealed record NetworkTrafficData(
    [property: Description("UTC time of the second sample.")] DateTimeOffset MeasuredAt,
    [property: Description("Monotonic elapsed time between the two samples, in milliseconds.")] long ElapsedMs,
    [property: Description("Per adapter, never summed (loopback, tunnel, VPN and virtual adapters carry the same traffic again).")] IReadOnlyList<NetworkTrafficEntry> Adapters,
    [property: Description("Identified adapters measured, including ones not returned.")] int Count,
    [property: Description("True when adapters were left out (limit 128 or the OS table exceeded the internal limit).")] bool Truncated);

/// <summary>
/// Administrative changes made by Sysacta in this process. A traffic window that overlaps one is not reported as a rate.
/// The generation is odd while a change runs.
/// </summary>
public sealed class NetworkChanges
{
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Marks a change as running until the returned scope is disposed (after the OS call really returned).</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _generation);
        return new Scope(this);
    }

    private sealed class Scope(NetworkChanges owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Increment(ref owner._generation);
            }
        }
    }
}

/// <summary>Two samples of the 64-bit interface counters inside one request, timed with the monotonic clock.</summary>
public static class NetworkTraffic
{
    /// <summary>Fixed; clients cannot choose the window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>An elapsed time outside (0, 4 s] is not used (the tool times out after 5 s).</summary>
    private static readonly TimeSpan MaxElapsed = TimeSpan.FromSeconds(4);

    public static async Task<NetworkTrafficData> MeasureAsync(INetworkPlatform platform, NetworkChanges changes, TimeProvider time, string? adapterId, bool byAlias, CancellationToken cancellationToken)
    {
        var generation = changes.Generation;
        var first = platform.ReadInterfaces();
        var startedAt = time.GetTimestamp();
        if (first.Problem is { } problem and not SystemMonitoring.UnavailableReason.Truncated)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The network interfaces could not be read.", retryable: problem != SystemMonitoring.UnavailableReason.PermissionDenied);
        }

        var before = NetworkAdapters.Identify(first);
        if (adapterId is not null && before.Find(adapterId) is null)
        {
            throw byAlias
                ? new ToolFailureException(ErrorCodes.DeviceDisconnected, "The network adapter registered under this alias is not present.")
                : new ToolFailureException(ErrorCodes.TargetNotFound, "The specified network adapter was not found. Use network_list_adapters for ids.");
        }

        await Task.Delay(Window, time, cancellationToken).ConfigureAwait(false);
        var second = platform.ReadInterfaces();
        var endedAt = time.GetTimestamp();
        var elapsed = time.GetElapsedTime(startedAt, endedAt);
        var changed = generation % 2 != 0 || changes.Generation != generation;
        var after = second.IsComplete || second.Problem == SystemMonitoring.UnavailableReason.Truncated ? NetworkAdapters.Identify(second) : null;

        var ids = before.Adapters.Select(a => a.Id);
        if (adapterId is null && after is not null)
        {
            ids = ids.Union(after.Adapters.Select(a => a.Id)).Order(StringComparer.Ordinal);
        }
        else if (adapterId is not null)
        {
            ids = [adapterId];
        }

        var all = ids.ToList();
        var entries = all.Take(NetworkAdapters.MaxListed)
            .Select(id => Entry(before.Find(id), after?.Find(id), before.Complete, after?.Complete == true, changed, endedAt - startedAt, elapsed, time.TimestampFrequency))
            .ToList();
        return new NetworkTrafficData(time.GetUtcNow(), (long)elapsed.TotalMilliseconds, entries, all.Count,
            all.Count > entries.Count || !before.Complete || (after is not null && !after.Complete));
    }

    /// <param name="beforeComplete">The first table was read completely (absence there means "added").</param>
    /// <param name="afterComplete">The second table was read completely (absence there means "removed").</param>
    internal static NetworkTrafficEntry Entry(NetworkAdapter? a, NetworkAdapter? b, bool beforeComplete, bool afterComplete, bool changed, long ticks, TimeSpan elapsed, long frequency)
    {
        var shown = b ?? a!;
        NetworkRateUnavailable? reason = (a, b) switch
        {
            (null, _) when !beforeComplete => NetworkRateUnavailable.SampleFailed,
            (_, null) when !afterComplete => NetworkRateUnavailable.SampleFailed,
            (null, _) => NetworkRateUnavailable.AdapterAdded,
            (_, null) => NetworkRateUnavailable.AdapterRemoved,
            _ when a.Row.Luid != b.Row.Luid || a.Row.Index != b.Row.Index => NetworkRateUnavailable.IdentityChanged,
            _ when a.Row.AdminStatus != b.Row.AdminStatus || a.Row.OperStatus != b.Row.OperStatus || a.Row.MediaConnectState != b.Row.MediaConnectState => NetworkRateUnavailable.StateChanged,
            _ when changed => NetworkRateUnavailable.ConfigurationChanged,
            _ when ticks <= 0 || elapsed > MaxElapsed => NetworkRateUnavailable.InvalidElapsed,
            _ when b.Row.InOctets < a.Row.InOctets || b.Row.OutOctets < a.Row.OutOctets => NetworkRateUnavailable.CounterDecreased,
            _ => null,
        };

        ulong? rxDelta = null, txDelta = null;
        long? rxRate = null, txRate = null;
        if (reason is null)
        {
            rxDelta = b!.Row.InOctets - a!.Row.InOctets;
            txDelta = b.Row.OutOctets - a.Row.OutOctets;
            rxRate = Rate(rxDelta.Value, ticks, frequency);
            txRate = Rate(txDelta.Value, ticks, frequency);
            if (rxRate is null || txRate is null)
            {
                (rxDelta, txDelta, rxRate, txRate, reason) = (null, null, null, null, NetworkRateUnavailable.InvalidData);
            }
        }

        return new NetworkTrafficEntry(shown.Id, shown.Name, shown.Kind, shown.OperState, shown.Row.InOctets, shown.Row.OutOctets, rxDelta, txDelta, rxRate, txRate, reason);
    }

    /// <summary>floor(bytes × 8 × frequency / ticks) in exact 128-bit arithmetic; null when it exceeds a long.</summary>
    internal static long? Rate(ulong bytes, long ticks, long frequency)
    {
        var bps = (UInt128)bytes * 8 * (UInt128)frequency / (UInt128)ticks;
        return bps <= long.MaxValue ? (long)bps : null;
    }
}
