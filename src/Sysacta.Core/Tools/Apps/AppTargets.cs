namespace Sysacta.Core.Tools.Apps;

/// <summary>
/// A resolved app (and process) target: the registry snapshot fixed before locking, re-checked after it. Lock keys are
/// <c>app:{id}</c> and, when a process is involved, <c>process:{pid}:{creation}</c>, shared by app_launch, app_close and
/// process_terminate (taken in ordinal order by the executor).
/// </summary>
public sealed record AppTarget(RegisteredApp App, int? Pid, long? CreationTime)
    : ToolTarget(LockKeysFor(App.Id, Pid, CreationTime), TargetIdFor(App.Id, Pid, CreationTime))
{
    private static string[] LockKeysFor(string appId, int? pid, long? creation) =>
        pid is { } p && creation is { } c ? [$"app:{appId}", $"process:{p}:{c}"] : [$"app:{appId}"];

    /// <summary>Audited as <c>target</c>: the registry id and, for a process, the OS pid and creation time. Nothing else.</summary>
    private static string TargetIdFor(string appId, int? pid, long? creation) =>
        pid is { } p && creation is { } c ? $"{appId}|{p}|{ProcessStartTime.ToText(c)}" : appId;
}

/// <summary>Shared registry lookups and the final, atomic target check of the app/process tools.</summary>
internal static class AppTargets
{
    public static RegisteredApp FindAllowed(IAppRegistry registry, string appId) =>
        registry.ListAllowed().FirstOrDefault(a => string.Equals(a.Id, appId, StringComparison.Ordinal))
        ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, "No allowed registered app has this app_id. Use app_list_available.");

    /// <summary>
    /// After the locks: the app must still be allowed with the generation that was resolved. Never switches to the newer
    /// approval: a re-approved app (other executable or arguments) refuses the old call.
    /// </summary>
    public static RegisteredApp Recheck(IAppRegistry registry, RegisteredApp resolved)
    {
        var current = registry.ListAllowed().FirstOrDefault(a => string.Equals(a.Id, resolved.Id, StringComparison.Ordinal))
            ?? throw new ToolFailureException(ErrorCodes.PermissionDenied, "The app was disabled or removed. Nothing was changed.");
        return current.Generation == resolved.Generation
            ? current
            : throw new ToolFailureException(ErrorCodes.TargetChanged, "The app's approval changed. Nothing was changed.");
    }

    /// <summary>For <see cref="ToolExecution.BeginSideEffect(Func{ToolError?})"/>: one short registry read inside the gate.</summary>
    public static Func<ToolError?> StillCurrent(IAppRegistry registry, RegisteredApp app) => () =>
        registry.IsCurrent(app.Id, app.Generation)
            ? null
            : new ToolError(ErrorCodes.PermissionDenied, "The app was disabled, removed or re-approved before the operation started. Nothing was changed.", false);

    public static string? SafeName(string? text) => UntrustedText.Sanitize(text, AppRules.MaxDisplayName);
}
