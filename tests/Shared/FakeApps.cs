using System.Collections.Concurrent;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Apps;

namespace Sysacta.Tests;

/// <summary>In-memory allow-list. Every read reflects the latest change, like the SQLite store.</summary>
public sealed class FakeAppRegistry : IAppRegistry
{
    public ConcurrentDictionary<string, RegisteredApp> Apps { get; } = new();

    private int _currentChecks;

    public int CurrentChecks => Volatile.Read(ref _currentChecks);

    public IReadOnlyList<RegisteredApp> ListAllowed() => [.. Apps.Values.Where(a => a.Allowed).OrderBy(a => a.Id, StringComparer.Ordinal)];

    public bool IsCurrent(string appId, long generation)
    {
        Interlocked.Increment(ref _currentChecks);
        return Apps.TryGetValue(appId, out var a) && a.Allowed && a.Generation == generation;
    }

    public static ExecutableIdentity Identity(int n) => new($"{n:x16}", $"{n:x32}", 1000 + n, new string((char)('a' + n % 6), 64));

    public RegisteredApp Add(int n, bool allowed = true, IReadOnlyList<string>? args = null, string? name = null)
    {
        var app = new RegisteredApp(AppRules.NewAppId(), name ?? $"App {n}", $@"C:\Apps\app{n}\app{n}.exe", args ?? [], allowed, 1, Identity(n));
        Apps[app.Id] = app;
        return app;
    }

    public void Disable(string id) => Apps[id] = Apps[id] with { Allowed = false };

    public void Reapprove(string id) => Apps[id] = Apps[id] with { Generation = Apps[id].Generation + 1, Allowed = true };

    public void Remove(string id) => Apps.TryRemove(id, out _);
}

/// <summary>A process as the fake OS sees it.</summary>
public sealed record FakeProcess(int Pid, long Creation, string Name, ExecutableIdentity? Image,
    bool? SameUser = true, int? Rid = 0x2000, bool? Protected = false, bool? Critical = false, bool IsSelf = false, bool IsParent = false, int Windows = 1);

/// <summary>Fake Windows adapter. Counts every OS operation; nothing real is launched, closed or terminated.</summary>
public sealed class FakeAppPlatform : IAppPlatform
{
    public bool IsElevated { get; set; }

    /// <summary>Inspection results by path; a missing path is NotFound.</summary>
    public ConcurrentDictionary<string, ExecutableCheck> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<int, FakeProcess> Processes { get; } = new();
    public int FailedCount { get; set; }
    public HashSet<string> Uncertain { get; } = new(StringComparer.Ordinal);

    public LaunchResult NextLaunch { get; set; } = new(LaunchStatus.Started, 4242, 133_000_000_000_000_000);
    public CloseRequestStatus NextClose { get; set; } = CloseRequestStatus.Posted;
    public bool TerminateAccepted { get; set; } = true;
    public bool ExitsAfterRequest { get; set; } = true;

    /// <summary>Whether removing a created but not resumed process is confirmed.</summary>
    public bool ReclaimSucceeds { get; set; } = true;

    public bool Truncated { get; set; }

    /// <summary>Hooks: run before the start point (prepare / open) or inside the OS write (may block or throw).</summary>
    public Action? OnPrepare { get; set; }
    public Action? OnOpen { get; set; }
    public Action? OnWrite { get; set; }

    private int _scans, _prepared, _launches, _opened, _closeRequests, _terminations, _disposed, _resumes, _reclaims;

    /// <summary>Created processes that were resumed (could run) / removed before running.</summary>
    public int Resumes => Volatile.Read(ref _resumes);
    public int Reclaims => Volatile.Read(ref _reclaims);

    public int Scans => Volatile.Read(ref _scans);
    public int Prepared => Volatile.Read(ref _prepared);
    public int Launches => Volatile.Read(ref _launches);
    public int Opened => Volatile.Read(ref _opened);
    public int CloseRequests => Volatile.Read(ref _closeRequests);
    public int Terminations => Volatile.Read(ref _terminations);
    public int Disposed => Volatile.Read(ref _disposed);

    public void AddFile(RegisteredApp app) => Files[app.Path] = new ExecutableCheck(ExecutableRefusal.None, app.Path, app.Identity with { Sha256 = "" });

    public FakeProcess Run(RegisteredApp? app, int pid, string? name = null, long creation = 0) =>
        Processes[pid] = new FakeProcess(pid, creation == 0 ? 133_000_000_000_000_000 + pid : creation, name ?? app?.FileName ?? "other.exe", app?.Identity);

    public ExecutableCheck Inspect(string path, bool computeHash, CancellationToken cancellationToken) =>
        Files.TryGetValue(path, out var check) ? check : ExecutableCheck.Refused(ExecutableRefusal.NotFound);

    public ProcessScan ScanSession(IReadOnlyList<AppMatchKey> apps, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scans);
        var facts = Processes.Values.Where(p => p.SameUser == true).OrderBy(p => p.Pid).Select(p => Facts(p, apps)).ToList();
        return new ProcessScan(facts, FailedCount, new HashSet<string>(Uncertain), Truncated);
    }

    public IPreparedLaunch PrepareLaunch(RegisteredApp app, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _prepared);
        OnPrepare?.Invoke();
        if (!Files.TryGetValue(app.Path, out var check) || check.Identity is not { } id || !id.SameFile(app.Identity) || id.Size != app.Identity.Size)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "changed");
        }

        return new PreparedLaunch(this);
    }

    public IOpenedProcess OpenProcess(int pid, long creationTime, IReadOnlyList<AppMatchKey> apps, bool terminate)
    {
        Interlocked.Increment(ref _opened);
        OnOpen?.Invoke();
        if (!Processes.TryGetValue(pid, out var p) || p.SameUser != true)
        {
            throw new ToolFailureException(ErrorCodes.TargetNotFound, "gone");
        }

        return p.Creation != creationTime
            ? throw new ToolFailureException(ErrorCodes.TargetChanged, "reused")
            : new OpenedProcess(this, p, Facts(p, apps));
    }

    private static ProcessFacts Facts(FakeProcess p, IReadOnlyList<AppMatchKey> apps) =>
        new(p.Pid, p.Creation, p.Name, p.SameUser, p.Rid, p.Protected, p.Critical, p.IsSelf, p.IsParent,
            p.Image is null ? null : apps.FirstOrDefault(a => a.Identity.SameFile(p.Image))?.AppId);

    private sealed class PreparedLaunch(FakeAppPlatform os) : IPreparedLaunch
    {
        public LaunchResult Start(Func<ToolError?> beforeResume)
        {
            Interlocked.Increment(ref os._launches);
            os.OnWrite?.Invoke();
            var next = os.NextLaunch;
            if (next.Status == LaunchStatus.Started && beforeResume() is { } refusal)
            {
                next = new LaunchResult(LaunchStatus.Withdrawn, next.Pid, next.CreationTime, os.ReclaimSucceeds, refusal);
            }

            if (next.Status == LaunchStatus.Started)
            {
                Interlocked.Increment(ref os._resumes);
            }
            else if (next.Reclaimed == true)
            {
                Interlocked.Increment(ref os._reclaims);
            }

            return next;
        }

        public void Dispose() => Interlocked.Increment(ref os._disposed);
    }

    private sealed class OpenedProcess(FakeAppPlatform os, FakeProcess process, ProcessFacts facts) : IOpenedProcess
    {
        public ProcessFacts Facts => facts;

        public int FindCloseableWindows() => process.Windows;

        public CloseRequestStatus RequestClose()
        {
            Interlocked.Increment(ref os._closeRequests);
            os.OnWrite?.Invoke();
            return os.NextClose;
        }

        public bool Terminate()
        {
            Interlocked.Increment(ref os._terminations);
            os.OnWrite?.Invoke();
            return os.TerminateAccepted;
        }

        public bool WaitForExit(TimeSpan timeout) => os.ExitsAfterRequest;

        public void Dispose() => Interlocked.Increment(ref os._disposed);
    }
}
