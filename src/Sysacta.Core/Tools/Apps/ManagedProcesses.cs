namespace Sysacta.Core.Tools.Apps;

/// <summary>How a process was associated with an allowed app.</summary>
public enum AppMatch
{
    /// <summary>Sysacta created it from the approved executable (image verified while suspended) under the current approval.</summary>
    Managed,
    /// <summary>
    /// Its image path currently names the approved file and that file looks unchanged since the process started. An
    /// observation only: never enough to close or terminate the process, and never reported as a verified instance.
    /// </summary>
    Estimated,
}

/// <summary>One process as the app/process tools report and judge it.</summary>
/// <param name="AppId">The managed app, else the estimated app, else null.</param>
/// <param name="Block">Null only for a managed process that passes every protection rule.</param>
public sealed record ProcessView(ProcessFacts Facts, string? AppId, AppMatch? Match, TerminateBlock? Block);

/// <summary>
/// The processes Sysacta itself created for app_launch (one instance per Sysacta process, in memory only). Only these can
/// be closed or terminated: Windows offers no documented way to prove from a limited process handle which file a running
/// image was loaded from, while a process Sysacta created was verified suspended against the locked, approved file before
/// it could run. An entry counts only while the app is allowed with the same approval generation (a re-approval or a
/// disable makes it unmanaged). Processes started before Sysacta started, by the user or by another program are never
/// managed. Bounded: the oldest entry is dropped when full (that process becomes unmanaged, which only refuses).
/// </summary>
public sealed class ManagedProcesses
{
    public const int Capacity = 256;
    private readonly Dictionary<(int Pid, long Creation), (string AppId, long Generation, long Sequence)> _entries = [];
    private long _sequence;

    /// <summary>
    /// Take before scanning: <see cref="Prune"/> then removes only entries added before this mark, so a process launched
    /// while the scan ran (and therefore missing from it) is never dropped.
    /// </summary>
    public long Mark()
    {
        lock (_entries)
        {
            return _sequence;
        }
    }

    public void Add(string appId, long generation, int pid, long creation)
    {
        lock (_entries)
        {
            _entries[(pid, creation)] = (appId, generation, ++_sequence);
            if (_entries.Count > Capacity)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.Sequence).Key);
            }
        }
    }

    public void Remove(int pid, long creation)
    {
        lock (_entries)
        {
            _entries.Remove((pid, creation));
        }
    }

    /// <summary>
    /// Drops entries added before <paramref name="mark"/> whose process is not in a complete scan: exited, or no longer
    /// inspectable (unmanaged is the safe side).
    /// </summary>
    public void Prune(ProcessScan scan, long mark)
    {
        if (scan.Truncated)
        {
            return;
        }

        var alive = scan.Processes.Select(p => (p.Pid, p.CreationTime)).ToHashSet();
        lock (_entries)
        {
            foreach (var key in _entries.Where(e => e.Value.Sequence <= mark && !alive.Contains(e.Key)).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>The app this exact process (pid + creation) was created for, while that app is allowed with the same generation.</summary>
    public string? ManagedAppId(ProcessFacts facts, IReadOnlyList<RegisteredApp> allowed)
    {
        (string AppId, long Generation, long Sequence) entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue((facts.Pid, facts.CreationTime), out entry))
            {
                return null;
            }
        }

        return allowed.Any(a => a.Id == entry.AppId && a.Generation == entry.Generation) ? entry.AppId : null;
    }

    public int Count
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Judges a process: only a managed process can pass; an estimated one is blocked as <see cref="TerminateBlock.NotManaged"/>.</summary>
    public ProcessView View(ProcessFacts facts, IReadOnlyList<RegisteredApp> allowed)
    {
        var managed = ManagedAppId(facts, allowed);
        var estimated = facts.EstimatedAppId;
        var block = ProcessRules.Evaluate(facts with { EstimatedAppId = managed });
        if (block == TerminateBlock.NotRegistered && estimated is not null)
        {
            block = TerminateBlock.NotManaged;
        }

        return new ProcessView(facts, managed ?? estimated, managed is not null ? AppMatch.Managed : estimated is not null ? AppMatch.Estimated : null, block);
    }
}
