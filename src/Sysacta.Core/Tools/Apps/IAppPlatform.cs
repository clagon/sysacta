namespace Sysacta.Core.Tools.Apps;

/// <summary>Result of checking an executable: the canonical path and identity, or why it cannot be used.</summary>
/// <param name="CanonicalPath">The handle's final path in plain <c>X:\...</c> form (registration stores this).</param>
/// <param name="Identity">Null when refused. Its Sha256 is empty when the content was not hashed (cheap check).</param>
public sealed record ExecutableCheck(ExecutableRefusal Refusal, string? CanonicalPath, ExecutableIdentity? Identity)
{
    public bool IsUsable => Refusal == ExecutableRefusal.None;

    public static ExecutableCheck Refused(ExecutableRefusal refusal) => new(refusal, null, null);
}

/// <summary>What a process scan matches against: the approved file object of an allowed app.</summary>
public sealed record AppMatchKey(string AppId, string FileName, ExecutableIdentity Identity)
{
    public static AppMatchKey For(RegisteredApp app) => new(app.Id, app.FileName, app.Identity);
}

/// <summary>
/// OS facts about one process of the current session, all read through the same process handle. Null means the fact
/// could not be read; every rule treats an unknown fact as a refusal.
/// </summary>
/// <param name="CreationTime">UTC FILETIME from GetProcessTimes.</param>
/// <param name="Name">Image file name (e.g. <c>notepad.exe</c>), from the OS snapshot. Display only.</param>
/// <param name="IntegrityRid">Mandatory integrity level RID of the process token (0x2000 = medium).</param>
/// <param name="AppId">The allowed app whose approved file object (volume + file id) is the process image; null otherwise.</param>
public sealed record ProcessFacts(
    int Pid,
    long CreationTime,
    string Name,
    bool? SameUser,
    int? IntegrityRid,
    bool? Protected,
    bool? Critical,
    bool IsSelf,
    bool IsParent,
    string? AppId);

/// <param name="Processes">Inspected processes of the current user in the current session, in snapshot order.</param>
/// <param name="FailedCount">Processes of the session whose user, creation time or state could not be read (not listed).</param>
/// <param name="UncertainAppIds">
/// Allowed apps for which a process with the same image name exists whose image identity could not be read: whether the
/// app is running is unknown.
/// </param>
/// <param name="Truncated">The OS snapshot had more processes than the internal limit.</param>
public sealed record ProcessScan(IReadOnlyList<ProcessFacts> Processes, int FailedCount, IReadOnlySet<string> UncertainAppIds, bool Truncated);

/// <summary>Why a process cannot be terminated or closed by Sysacta (wire names are snake_case).</summary>
public enum TerminateBlock
{
    /// <summary>Its image is not the approved executable of an allowed app.</summary>
    NotRegistered,
    Self,
    /// <summary>The process that started Sysacta.</summary>
    SysactaParent,
    OtherUser,
    Protected,
    Critical,
    /// <summary>High or system integrity (elevated).</summary>
    Elevated,
    /// <summary>Below medium integrity (sandboxed / AppContainer).</summary>
    LowIntegrity,
    /// <summary>A required fact could not be read.</summary>
    Unverified,
}

public static class ProcessRules
{
    private const int MediumRid = 0x2000;
    private const int HighRid = 0x3000;

    /// <summary>
    /// Null when the process may be closed/terminated: an allowed app's exact image, same user and session, medium
    /// integrity, not protected or critical, not Sysacta or its parent. Unknown facts refuse.
    /// </summary>
    public static TerminateBlock? Evaluate(ProcessFacts f) =>
        f.IsSelf ? TerminateBlock.Self
        : f.IsParent ? TerminateBlock.SysactaParent
        : f.SameUser is not { } sameUser ? TerminateBlock.Unverified
        : !sameUser ? TerminateBlock.OtherUser
        : f.Protected is not { } isProtected || f.Critical is not { } critical || f.IntegrityRid is not { } rid ? TerminateBlock.Unverified
        : isProtected ? TerminateBlock.Protected
        : critical ? TerminateBlock.Critical
        : rid >= HighRid ? TerminateBlock.Elevated
        : rid < MediumRid ? TerminateBlock.LowIntegrity
        : f.AppId is null ? TerminateBlock.NotRegistered
        : null;
}

/// <summary>Outcome of creating the process (no retry, no fallback).</summary>
public enum LaunchStatus
{
    /// <summary>Created, verified suspended (same image), allowed by the pre-resume check and resumed.</summary>
    Started,
    /// <summary>CreateProcess refused: no process was created.</summary>
    Failed,
    /// <summary>The executable requires elevation (UAC); no process was created and no elevation was requested.</summary>
    ElevationRequired,
    /// <summary>The created process did not have the verified image; it was not resumed (see <see cref="LaunchResult.Reclaimed"/>).</summary>
    ImageMismatch,
    /// <summary>The pre-resume check refused (cancelled, timed out, authorization or approval changed); not resumed.</summary>
    Withdrawn,
    /// <summary>ResumeThread failed; the process was not resumed.</summary>
    ResumeFailed,
}

/// <param name="Reclaimed">
/// For a created but not resumed process: true when TerminateProcess on its own handle succeeded and the handle signaled
/// exit (it never ran any of its code); false when that could not be confirmed. Null when no process remains to reclaim.
/// </param>
/// <param name="Refusal">For <see cref="LaunchStatus.Withdrawn"/>: the refusal of the pre-resume check.</param>
public sealed record LaunchResult(LaunchStatus Status, int? Pid = null, long? CreationTime = null, bool? Reclaimed = null, ToolError? Refusal = null);

/// <summary>
/// An executable opened and verified for one launch. While it exists, the file is held without write/delete sharing and
/// every parent folder without delete sharing, so neither the file nor the path to it can be modified, replaced or
/// renamed until <see cref="Start"/> returns. Dispose releases the handles.
/// </summary>
public interface IPreparedLaunch : IDisposable
{
    /// <summary>
    /// Creates the process once, suspended, and verifies its image. Then runs <paramref name="beforeResume"/> (the final
    /// cancellation / authorization / approval check); only when it returns null is the process resumed. Otherwise the
    /// process this call created is terminated through its own handle and waited on (bounded). Call only right after the
    /// start point. Everything that can fail without side effects (paths, command line) is prepared before.
    /// </summary>
    LaunchResult Start(Func<ToolError?> beforeResume);
}

public enum CloseRequestStatus
{
    /// <summary>WM_CLOSE was posted to the window.</summary>
    Posted,
    /// <summary>The window no longer belongs to the process; nothing was sent.</summary>
    WindowGone,
    /// <summary>Posting failed; nothing was sent.</summary>
    Failed,
}

/// <summary>
/// One process opened by pid and verified to have the expected creation time. The handle stays open until disposed, so
/// the pid cannot be reused meanwhile and every operation acts on this same process.
/// </summary>
public interface IOpenedProcess : IDisposable
{
    /// <summary>Facts read through this handle when it was opened.</summary>
    ProcessFacts Facts { get; }

    /// <summary>
    /// Number of visible, unowned, non-tool top-level windows of the process; when exactly one, it is remembered for
    /// <see cref="RequestClose"/>.
    /// </summary>
    int FindCloseableWindows();

    /// <summary>Re-checks that the remembered window still belongs to the process, then posts WM_CLOSE once (never waits on the app).</summary>
    CloseRequestStatus RequestClose();

    /// <summary>TerminateProcess on this handle with a fixed exit code. False when Windows refused (nothing terminated).</summary>
    bool Terminate();

    /// <summary>Waits on this handle; true when the process has exited.</summary>
    bool WaitForExit(TimeSpan timeout);
}

/// <summary>
/// The Windows adapter for apps and processes. Synchronous; the executor runs it on worker threads. Failures that are not
/// described by a result throw <see cref="ToolFailureException"/> with a fixed message (no paths or OS text).
/// </summary>
public interface IAppPlatform
{
    /// <summary>
    /// True when Sysacta runs elevated or above medium integrity. A launched child would inherit that token, so app_launch
    /// is unavailable (spec §4: no unintended inheritance of administrator rights). Cheap, cached.
    /// </summary>
    bool IsElevated { get; }

    /// <summary>Applies every executable rule through a handle. Hashes the whole file when <paramref name="computeHash"/>.</summary>
    ExecutableCheck Inspect(string path, bool computeHash, CancellationToken cancellationToken);

    /// <summary>Processes of the current user in the current session, matched against <paramref name="apps"/>.</summary>
    ProcessScan ScanSession(IReadOnlyList<AppMatchKey> apps, CancellationToken cancellationToken);

    /// <summary>
    /// Opens and re-verifies <paramref name="app"/>'s executable (all rules, the approved identity and SHA-256) and keeps it
    /// locked. Throws <see cref="ToolFailureException"/> (TARGET_CHANGED / NOT_SUPPORTED / OS_API_ERROR) when refused.
    /// </summary>
    IPreparedLaunch PrepareLaunch(RegisteredApp app, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the process with the minimum rights for the operation (query, synchronize, and terminate only when
    /// <paramref name="terminate"/>) and verifies its creation time. Throws TARGET_NOT_FOUND when it has exited and
    /// TARGET_CHANGED when the pid now belongs to another process.
    /// </summary>
    IOpenedProcess OpenProcess(int pid, long creationTime, IReadOnlyList<AppMatchKey> apps, bool terminate);
}
