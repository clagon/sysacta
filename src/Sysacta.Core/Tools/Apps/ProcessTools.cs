using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Sysacta.Core.Tools.Apps;

public sealed class ProcessListArgs
{
    [Description("Maximum number of processes to return, 1-256. Default 100.")]
    [Range(1, ProcessListRunningTool.MaxLimit)]
    [DefaultValue(ProcessListRunningTool.DefaultLimit)]
    public int? Limit { get; init; }

    [Description("Optional case-insensitive substring of the process name (e.g. \"note\"). Plain text only: no wildcards, regular expressions or paths.")]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression(@".*\S.*")]
    [NoControlCharacters]
    public string? Filter { get; init; }
}

public sealed class ProcessTerminateArgs
{
    [Description("Process id from process_list_running or app_list_running.")]
    [Range(1, int.MaxValue)]
    public required int Pid { get; init; }

    [Description("The exact start_time returned with that pid (UTC, yyyy-MM-ddTHH:mm:ss.fffffffZ). Guards against pid reuse; no other format or rounding is accepted.")]
    [StringLength(28, MinimumLength = 28)]
    [RegularExpression(ProcessStartTime.Pattern)]
    public required string StartTime { get; init; }
}

public sealed record ProcessEntry(
    int Pid,
    [property: Description("Exact process creation time (UTC, 100 ns).")] string StartTime,
    [property: Description("Image file name; null when it cannot be shown safely.")] string? Name,
    [property: Description("The allowed app this process belongs to (see app_match); null otherwise.")] string? AppId,
    [property: Description("managed = launched by Sysacta under the current approval (can be terminated); estimated = looks like the app's approved executable but not launched by Sysacta (never terminated); null when no app.")] AppMatch? AppMatch,
    [property: Description("Observed now; process_terminate re-verifies everything.")] bool CanTerminate,
    TerminateBlock? TerminateBlockedReason);

public sealed record ProcessListData(
    IReadOnlyList<ProcessEntry> Processes,
    [property: Description("Processes matching the filter before the limit.")] int Total,
    bool Truncated,
    [property: Description("Processes of this session that could not be inspected (not listed).")] int FailedCount);

public sealed record ProcessTerminateData(
    int Pid,
    string StartTime,
    string AppId,
    [property: Description("True when Windows accepted the termination; false when nothing was terminated; null when unknown.")] bool? Terminated,
    [property: Description("True when the process handle signaled exit within 2 seconds.")] bool? Exited);

/// <summary>Processes of the current user in the current session (not services, other users or other sessions).</summary>
public sealed class ProcessListRunningTool(IAppRegistry registry, IAppPlatform platform, ManagedProcesses managed) : SysactaTool<ProcessListArgs, ProcessListData>
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 256;

    public override ToolDescriptor Descriptor { get; } = new("process_list_running", ToolCategories.Apps, "List running processes",
        "Lists processes of the current Windows user in the current session (not services, other users or other sessions), sorted by name then pid: pid, exact start_time, image name, the allowed app_id with app_match (managed = launched by Sysacta, estimated = only observed), and whether process_terminate would accept it now (only managed processes). filter is a plain case-insensitive substring of the name. Command lines, paths, window titles, user names and environment are never returned.",
        DefaultPolicy.On, ReadOnly: true, AuditedArguments: ["limit"]);

    protected override Task<ToolOutcome<ProcessListData>> ExecuteAsync(ProcessListArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var apps = registry.ListAllowed();
        var scan = platform.ScanSession([.. apps.Select(AppMatchKey.For)], cancellationToken);
        managed.Prune(scan);
        var matching = scan.Processes
            .Select(p => (Facts: p, Name: UntrustedText.Sanitize(p.Name, 64)))
            .Where(p => args.Filter is null || (p.Name?.Contains(args.Filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Facts.Pid)
            .ToList();
        var limit = args.Limit ?? DefaultLimit;
        var processes = matching.Take(limit).Select(p =>
        {
            var view = managed.View(p.Facts, apps);
            return new ProcessEntry(p.Facts.Pid, ProcessStartTime.ToText(p.Facts.CreationTime), p.Name, view.AppId, view.Match, view.Block is null, view.Block);
        }).ToList();
        return Task.FromResult(new ToolOutcome<ProcessListData>(
            new ProcessListData(processes, matching.Count, scan.Truncated || matching.Count > processes.Count, scan.FailedCount),
            Verification.NotApplicable));
    }
}

/// <summary>Forcibly terminates one process of an allowed app, identified by pid and exact creation time, through one verified handle.</summary>
public sealed class ProcessTerminateTool(IAppRegistry registry, IAppPlatform platform, ManagedProcesses managed) : SysactaTool<ProcessTerminateArgs, ProcessTerminateData>
{
    public override ToolDescriptor Descriptor { get; } = new("process_terminate", ToolCategories.Apps, "Force-terminate app process",
        "Forcibly terminates one process (TerminateProcess) given its pid and exact start_time. UNSAVED DATA IN THAT PROCESS IS LOST; prefer app_close. Only processes Sysacta launched with app_launch for an allowed app under its current approval (app_match=managed; since Sysacta started), owned by the current user in the current session, at normal integrity, and not protected, critical, Sysacta or its parent. Processes the user started, processes from before Sysacta started or before a re-approval are never terminated. Child processes, other instances and process trees are never terminated. A reused pid (other start_time) is refused with TARGET_CHANGED and an exited process with TARGET_NOT_FOUND. verification=verified when the process handle signaled exit. Never retried; do not retry when status=unknown.",
        DefaultPolicy.Off, ReadOnly: false, AuditedArguments: ["pid"]);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(ProcessTerminateArgs args, CancellationToken cancellationToken)
    {
        if (!ProcessStartTime.TryParse(args.StartTime, out var creation))
        {
            throw new ToolFailureException(ErrorCodes.InvalidArgument, "start_time is not a valid exact process start time.");
        }

        var apps = registry.ListAllowed();
        var scan = platform.ScanSession([.. apps.Select(AppMatchKey.For)], cancellationToken);
        var samePid = scan.Processes.Where(p => p.Pid == args.Pid).ToList();
        managed.Prune(scan);
        var facts = samePid.FirstOrDefault(p => p.CreationTime == creation) ?? throw (samePid.Count > 0
            ? new ToolFailureException(ErrorCodes.TargetChanged, "The pid now belongs to another process. Nothing was terminated.")
            : new ToolFailureException(ErrorCodes.TargetNotFound, "No such process of the current user and session is running. Nothing was terminated."));
        if (managed.View(facts, apps) is not { Match: AppMatch.Managed, Block: null } view || apps.FirstOrDefault(a => a.Id == view.AppId) is not { } app)
        {
            throw new ToolFailureException(ErrorCodes.PermissionDenied, "This process cannot be terminated by Sysacta (see terminate_blocked_reason in process_list_running). Nothing was terminated.");
        }

        return ValueTask.FromResult<ToolTarget>(new AppTarget(app, facts.Pid, facts.CreationTime));
    }

    protected override Task<ToolOutcome<ProcessTerminateData>> ExecuteAsync(ProcessTerminateArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var target = (AppTarget)execution.Target;
        var app = AppTargets.Recheck(registry, target.App);
        var (pid, creation) = (target.Pid!.Value, target.CreationTime!.Value);
        using var process = platform.OpenProcess(pid, creation, [AppMatchKey.For(app)], terminate: true);
        if (managed.View(process.Facts, [app]) is not { Match: AppMatch.Managed, Block: null } view || view.AppId != app.Id)
        {
            throw new ToolFailureException(ErrorCodes.PermissionDenied, "This process cannot be terminated by Sysacta. Nothing was terminated.");
        }

        var unknown = new ProcessTerminateData(pid, ProcessStartTime.ToText(creation), app.Id, null, null);
        ReportProgress(execution, unknown);
        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect(AppTargets.StillCurrent(registry, app));
        if (!process.Terminate())
        {
            return Task.FromResult(ToolOutcome.Error(unknown with { Terminated = false }, ErrorCodes.OsApiError, "Windows refused to terminate the process. Nothing was terminated."));
        }

        var accepted = unknown with { Terminated = true };
        ReportAccepted(execution, accepted);
        if (process.WaitForExit(AppCloseTool.ExitWait))
        {
            managed.Remove(pid, creation);
            return Task.FromResult(new ToolOutcome<ProcessTerminateData>(accepted with { Exited = true }, Verification.Verified));
        }

        return Task.FromResult(new ToolOutcome<ProcessTerminateData>(accepted with { Exited = false }, Verification.Accepted));
    }
}
