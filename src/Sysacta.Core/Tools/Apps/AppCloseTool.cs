using System.ComponentModel;

namespace Sysacta.Core.Tools.Apps;

public sealed record AppCloseData(
    string AppId,
    [property: Description("False when no instance was running (nothing to close).")] bool WasRunning,
    int? Pid,
    string? StartTime,
    [property: Description("True when the close request (WM_CLOSE) was posted; false when nothing was sent; null when unknown.")] bool? CloseRequested,
    [property: Description("True when the process handle signaled exit within 2 seconds; false when still running (the app may ask to save); null when unknown.")] bool? Exited);

/// <summary>Asks the single top-level window of the single running instance to close (WM_CLOSE). Never forces.</summary>
public sealed class AppCloseTool(IAppRegistry registry, IAppPlatform platform) : SysactaTool<AppIdArgs, AppCloseData>
{
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    public override ToolDescriptor Descriptor { get; } = new("app_close", ToolCategories.Apps, "Close registered app",
        "Asks a running allowed app to close normally, like clicking its close button: posts one close request to its window. Only when the app has exactly one running process (of the current user, normal integrity) with exactly one top-level window; otherwise nothing is sent (AMBIGUOUS_TARGET / NOT_SUPPORTED). The app may ask to save, refuse or close later: verification=accepted with exited=false means the request was delivered but the process is still running. Never forces termination (that is process_terminate). If no instance is running, returns was_running=false.",
        DefaultPolicy.Off, ReadOnly: false, AuditedArguments: []);

    protected override ValueTask<ToolTarget> ResolveTargetAsync(AppIdArgs args, CancellationToken cancellationToken)
    {
        var app = AppTargets.FindAllowed(registry, args.AppId);
        var instance = SingleInstance(app, cancellationToken);
        return ValueTask.FromResult<ToolTarget>(new AppTarget(app, instance?.Pid, instance?.CreationTime));
    }

    protected override Task<ToolOutcome<AppCloseData>> ExecuteAsync(AppIdArgs args, ToolExecution execution, CancellationToken cancellationToken)
    {
        var target = (AppTarget)execution.Target;
        var app = AppTargets.Recheck(registry, target.App);
        if (target.Pid is not { } pid || target.CreationTime is not { } creation)
        {
            // Confirmed again under the app lock: never switch to an instance started meanwhile.
            return Task.FromResult(SingleInstance(app, cancellationToken) is null
                ? new ToolOutcome<AppCloseData>(new AppCloseData(app.Id, false, null, null, false, null), Verification.Verified)
                : throw new ToolFailureException(ErrorCodes.TargetChanged, "The app started while the request waited. Nothing was sent.", retryable: true));
        }

        var start = ProcessStartTime.ToText(creation);
        using var process = platform.OpenProcess(pid, creation, [AppMatchKey.For(app)], terminate: false);
        if (process.Facts.AppId != app.Id || ProcessRules.Evaluate(process.Facts) is not null)
        {
            throw new ToolFailureException(ErrorCodes.PermissionDenied, "This process cannot be closed by Sysacta. Nothing was sent.");
        }

        var windows = process.FindCloseableWindows();
        if (windows != 1)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, windows == 0
                ? "The app has no top-level window to close (it may run in the background). Nothing was sent."
                : "The app has several top-level windows; app_close supports exactly one. Nothing was sent.");
        }

        var unknown = new AppCloseData(app.Id, true, pid, start, null, null);
        ReportProgress(execution, unknown);
        cancellationToken.ThrowIfCancellationRequested();
        execution.BeginSideEffect(AppTargets.StillCurrent(registry, app));
        var sent = process.RequestClose();
        if (sent != CloseRequestStatus.Posted)
        {
            return Task.FromResult(ToolOutcome.Error(unknown with { CloseRequested = false }, sent == CloseRequestStatus.WindowGone ? ErrorCodes.TargetChanged : ErrorCodes.OsApiError,
                sent == CloseRequestStatus.WindowGone ? "The window closed or changed before the request. Nothing was sent." : "Windows did not accept the close request. Nothing was sent."));
        }

        var accepted = unknown with { CloseRequested = true };
        ReportAccepted(execution, accepted);
        return Task.FromResult(process.WaitForExit(ExitWait)
            ? new ToolOutcome<AppCloseData>(accepted with { Exited = true }, Verification.Verified)
            : new ToolOutcome<AppCloseData>(accepted with { Exited = false }, Verification.Accepted));
    }

    /// <summary>The only instance, null when none runs; refuses when several run or the state is unknown.</summary>
    private ProcessFacts? SingleInstance(RegisteredApp app, CancellationToken cancellationToken)
    {
        var scan = platform.ScanSession([AppMatchKey.For(app)], cancellationToken);
        if (scan.UncertainAppIds.Contains(app.Id))
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Could not identify the app's processes. Nothing was sent.", retryable: true);
        }

        var instances = scan.Processes.Where(p => p.AppId == app.Id).Take(2).ToList();
        return instances.Count switch
        {
            0 => null,
            1 => instances[0],
            _ => throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The app has several running processes; app_close supports exactly one. Nothing was sent. Use app_list_running and process_terminate only if data loss is acceptable."),
        };
    }
}
