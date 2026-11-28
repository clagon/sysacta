using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Sysacta.Core.Tools.Apps;

public sealed class AppNoArgs;

public sealed class AppIdArgs
{
    [Description("Opaque app id from app_list_available (app_ followed by 32 lowercase hex digits). Names, paths and pids are not accepted.")]
    [StringLength(36, MinimumLength = 36)]
    [RegularExpression(AppRules.AppIdPattern)]
    public required string AppId { get; init; }
}

/// <summary>Current state of a registered app's executable from a cheap check (no hash). The content is verified only at launch.</summary>
public enum AppStatus
{
    /// <summary>Same file (volume, file id, size) as approved, and all rules pass.</summary>
    Ready,
    /// <summary>Another file, size or path form is now at the approved location: the local user must re-approve it.</summary>
    TargetChanged,
    TargetMissing,
    /// <summary>The executable no longer passes the safety rules.</summary>
    NotSupported,
    /// <summary>The check could not be completed now.</summary>
    CheckFailed,
}

public enum LaunchBlock
{
    /// <summary>Sysacta runs elevated; a launched app would inherit administrator rights.</summary>
    SysactaElevated,
}

public sealed record AvailableApp(
    [property: Description("Opaque, permanent id for app_launch / app_close.")] string AppId,
    [property: Description("Display name set by the local user; null when it cannot be shown safely.")] string? DisplayName,
    [property: Description("Always win32_exe (a registered local executable).")] string Kind,
    [property: Description("ready = the approved file is in place (content is verified at launch); target_changed / target_missing / not_supported need the local user; check_failed is temporary.")] AppStatus Status,
    [property: Description("True when status is ready and launching is available in this environment.")] bool Launchable);

public sealed record AppListAvailableData(
    IReadOnlyList<AvailableApp> Apps,
    int Count,
    bool Truncated,
    [property: Description("False when app_launch is disabled in this environment (see launch_blocked_reason).")] bool LaunchAvailable,
    LaunchBlock? LaunchBlockedReason);

public sealed record RunningAppInstance(
    string AppId,
    string? DisplayName,
    [property: Description("managed = Sysacta launched this process under the current approval (it can be closed or terminated); estimated = its image looks like the approved file but Sysacta did not start it (observation only, never closed or terminated).")] AppMatch AppMatch,
    int Pid,
    [property: Description("Exact process creation time (UTC, 100 ns). Pass with pid to process_terminate.")] string StartTime,
    [property: Description("Observed now; process_terminate re-verifies everything.")] bool CanTerminate,
    TerminateBlock? TerminateBlockedReason);

public sealed record AppListRunningData(
    IReadOnlyList<RunningAppInstance> Instances,
    int Count,
    bool Truncated,
    [property: Description("Apps whose running state is unknown: a process with the same image name could not be inspected.")] IReadOnlyList<string> UncertainAppIds,
    [property: Description("Processes of this session that could not be inspected (not listed).")] int FailedCount);

public sealed record AppLaunchData(
    string AppId,
    [property: Description("True when an instance was already running: nothing was launched (no focus or window change).")] bool AlreadyRunning,
    [property: Description("True when Windows created the process; false when nothing was started; null when unknown.")] bool? Launched,
    int? Pid,
    string? StartTime);

/// <summary>Allowed registered apps (never unallowed ones, candidates, paths, arguments or hashes).</summary>
public sealed class AppListAvailableTool(IAppRegistry registry, IAppPlatform platform) : SysactaTool<AppNoArgs, AppListAvailableData>
{
    public override ToolDescriptor Descriptor { get; } = new("app_list_available", ToolCategories.Apps, "List launchable apps",
        "Lists the apps the local user registered and allowed for launching, with an opaque app_id each. Only these can be launched or closed; apps cannot be added, allowed or changed over MCP. status is a cheap check of the approved file (not a content hash): ready does not guarantee that a launch succeeds. Paths, arguments and hashes are never returned.",
        DefaultPolicy.On, ReadOnly: true, AuditedArguments: []);

    protected override Task<ToolOutcome<AppListAvailableData>> ExecuteAsync(AppNoArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var elevated = platform.IsElevated;
        var all = registry.ListAllowed();
        var apps = new List<AvailableApp>();
        foreach (var app in all.Take(AppRules.MaxApps))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = StatusOf(platform, app, cancellationToken);
            apps.Add(new AvailableApp(app.Id, AppTargets.SafeName(app.DisplayName), AppRules.Win32ExeKind, status, status == AppStatus.Ready && !elevated));
        }

        return Task.FromResult(new ToolOutcome<AppListAvailableData>(
            new AppListAvailableData(apps, apps.Count, all.Count > apps.Count, !elevated, elevated ? LaunchBlock.SysactaElevated : null),
            Verification.NotApplicable));
    }

    /// <summary>Cheap check (no hash) of the approved file; also used by the console.</summary>
    public static AppStatus StatusOf(IAppPlatform platform, RegisteredApp app, CancellationToken cancellationToken)
    {
        ExecutableCheck check;
        try
        {
            check = platform.Inspect(app.Path, computeHash: false, cancellationToken);
        }
        catch (ToolFailureException)
        {
            return AppStatus.CheckFailed;
        }

        return check.Refusal switch
        {
            ExecutableRefusal.None => check.Identity is { } id && id.SameFile(app.Identity) && id.Size == app.Identity.Size
                && string.Equals(check.CanonicalPath, app.Path, StringComparison.OrdinalIgnoreCase) ? AppStatus.Ready : AppStatus.TargetChanged,
            ExecutableRefusal.NotFound => AppStatus.TargetMissing,
            ExecutableRefusal.IdentityChanged or ExecutableRefusal.PathMismatch or ExecutableRefusal.ReparsePoint => AppStatus.TargetChanged,
            ExecutableRefusal.Unreadable => AppStatus.CheckFailed,
            _ => AppStatus.NotSupported,
        };
    }
}

/// <summary>Running instances of allowed apps: managed (launched by Sysacta) or estimated (observed).</summary>
public sealed class AppListRunningTool(IAppRegistry registry, IAppPlatform platform, ManagedProcesses managed) : SysactaTool<AppNoArgs, AppListRunningData>
{
    public const int MaxInstances = 256;

    public override ToolDescriptor Descriptor { get; } = new("app_list_running", ToolCategories.Apps, "List running apps",
        "Lists running processes of the allowed apps in the current user's session, one entry per process, with pid and exact start_time. app_match=managed: Sysacta launched the process from the approved executable under the current approval (since Sysacta started); only these can be closed or terminated. app_match=estimated: its image looks like the approved executable but Sysacta did not start it (started by the user, before Sysacta started, or before a re-approval); listed for information only. Never matched by name, title or pid; child processes running other executables are not included. uncertain_app_ids lists apps whose state could not be determined. No paths, command lines, window titles or user ids are returned.",
        DefaultPolicy.On, ReadOnly: true, AuditedArguments: []);

    protected override Task<ToolOutcome<AppListRunningData>> ExecuteAsync(AppNoArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var apps = registry.ListAllowed();
        var names = apps.ToDictionary(a => a.Id, a => AppTargets.SafeName(a.DisplayName), StringComparer.Ordinal);
        var scan = platform.ScanSession([.. apps.Select(AppMatchKey.For)], cancellationToken);
        managed.Prune(scan);
        var matched = scan.Processes.Select(p => managed.View(p, apps)).Where(v => v.AppId is not null && names.ContainsKey(v.AppId))
            .OrderBy(v => v.AppId, StringComparer.Ordinal).ThenBy(v => v.Facts.CreationTime).ThenBy(v => v.Facts.Pid)
            .ToList();
        var instances = matched.Take(MaxInstances).Select(v => new RunningAppInstance(v.AppId!, names[v.AppId!], v.Match!.Value, v.Facts.Pid,
            ProcessStartTime.ToText(v.Facts.CreationTime), v.Block is null, v.Block)).ToList();
        return Task.FromResult(new ToolOutcome<AppListRunningData>(
            new AppListRunningData(instances, instances.Count, scan.Truncated || matched.Count > instances.Count,
                [.. scan.UncertainAppIds.Where(names.ContainsKey).Order(StringComparer.Ordinal)], scan.FailedCount),
            Verification.NotApplicable));
    }
}

/// <summary>Launches the approved executable of an allowed app with its approved fixed arguments, once.</summary>
public sealed class AppLaunchTool(IAppRegistry registry, IAppPlatform platform, ManagedProcesses managed) : SysactaTool<AppIdArgs, AppLaunchData>
{
    public override ToolDescriptor Descriptor { get; } = new("app_launch", ToolCategories.Apps, "Launch registered app",
        "Launches an allowed registered app by app_id with the executable file and fixed arguments the local user approved; nothing else (path, arguments, URL, environment, working folder) can be chosen. If an instance that Sysacta launched is still running, nothing is launched and already_running=true is returned (verified; no window is focused). If an instance that Sysacta did not launch appears to be running (app_match=estimated in app_list_running), nothing is launched and AMBIGUOUS_TARGET is returned. The launched process becomes managed: app_close and process_terminate act only on such processes. The executable is re-verified (same file and SHA-256) right before the launch; a changed file is refused until the local user re-approves it. status=success with verification=accepted means Windows created the process, not that the app is ready or keeps running (some apps hand over to another process and exit). Never elevates. Do not retry when status=unknown.",
        DefaultPolicy.On, ReadOnly: false, AuditedArguments: [], Timeout: TimeSpan.FromSeconds(15));

    public override ToolAvailability GetAvailability() => platform.IsElevated
        ? ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Sysacta runs elevated; launched apps would inherit administrator rights. Run Sysacta as a standard user to launch apps.")
        : ToolAvailability.Available;

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AppIdArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolTarget>(new AppTarget(AppTargets.FindAllowed(registry, args.AppId), null, null));

    protected override Task<ToolOutcome<AppLaunchData>> ExecuteAsync(AppIdArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var app = AppTargets.Recheck(registry, ((AppTarget)execution.Target).App);
        if (platform.IsElevated)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "Sysacta runs elevated; launching apps is disabled. Nothing was launched.");
        }

        var scan = platform.ScanSession([AppMatchKey.For(app)], cancellationToken);
        managed.Prune(scan);
        var views = scan.Processes.Select(p => managed.View(p, [app])).Where(v => v.AppId == app.Id).ToList();
        if (views.Any(v => v.Match == AppMatch.Managed))
        {
            return Task.FromResult(new ToolOutcome<AppLaunchData>(new AppLaunchData(app.Id, true, false, null, null), Verification.Verified));
        }

        if (scan.Truncated || scan.UncertainAppIds.Contains(app.Id))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Could not determine whether the app is already running. Nothing was launched.", retryable: true);
        }

        if (views.Count > 0)
        {
            // Never reported as verified, never launched a second time without knowing.
            throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "An instance of the app that Sysacta did not launch appears to be running (started by the user, before Sysacta started, or before a re-approval). Nothing was launched.");
        }

        using var prepared = platform.PrepareLaunch(app, cancellationToken);
        ReportProgress(execution, new AppLaunchData(app.Id, false, null, null, null));
        cancellationToken.ThrowIfCancellationRequested();
        var stillCurrent = AppTargets.StillCurrent(registry, app);
        execution.BeginSideEffect(stillCurrent);
        // The process is created suspended; it runs only if the call is still not cancelled, detached or refused then.
        var result = prepared.Start(() => execution.BeginNextSideEffect(stillCurrent, cancellationToken));
        var notStarted = new AppLaunchData(app.Id, false, false, null, null);
        if (result.Status is (LaunchStatus.ImageMismatch or LaunchStatus.Withdrawn or LaunchStatus.ResumeFailed) && result.Reclaimed != true)
        {
            return Task.FromResult(ToolOutcome.Unknown(new AppLaunchData(app.Id, false, null, result.Pid, null), ErrorCodes.OsApiError,
                "A suspended process was created but could not be confirmed removed. Whether it runs is unknown; do not retry."));
        }

        if (result.Status == LaunchStatus.Started && result.Pid is { } pid && result.CreationTime is { } created)
        {
            managed.Add(app.Id, app.Generation, pid, created);
        }

        return Task.FromResult(result.Status switch
        {
            LaunchStatus.Started => new ToolOutcome<AppLaunchData>(
                new AppLaunchData(app.Id, false, true, result.Pid, result.CreationTime is { } c ? ProcessStartTime.ToText(c) : null), Verification.Accepted),
            LaunchStatus.ElevationRequired => ToolOutcome.Error(notStarted, ErrorCodes.NotSupported, "The app requires administrator rights and Sysacta never elevates. Nothing was started."),
            LaunchStatus.ImageMismatch => ToolOutcome.Error(notStarted, ErrorCodes.TargetChanged, "The created process did not run the approved executable; it was removed before it ran."),
            LaunchStatus.Withdrawn => ToolOutcome.Error(notStarted, result.Refusal?.Code ?? ErrorCodes.PermissionDenied, "The launch was cancelled or refused before the app ran; the suspended process was removed."),
            LaunchStatus.ResumeFailed => ToolOutcome.Error(notStarted, ErrorCodes.OsApiError, "Windows could not start the created process; it was removed before it ran."),
            _ => ToolOutcome.Error(notStarted, ErrorCodes.OsApiError, "Windows did not start the app. Nothing was started."),
        });
    }
}
