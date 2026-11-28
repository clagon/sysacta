namespace Sysacta.Core.Tools;

/// <summary>Initial global permission of a tool on first install (spec §7: ON / OFF / EXP).</summary>
public enum DefaultPolicy
{
    /// <summary>Everyday operation: enabled globally on first install.</summary>
    On,
    /// <summary>Potentially disruptive: requires explicit enabling.</summary>
    Off,
    /// <summary>Experimental / unofficial API: disabled and flagged in the UI.</summary>
    Experimental,
}

/// <summary>
/// Static metadata for a tool. Everything the pipeline needs to decide whether, and how, a tool may run.
/// </summary>
/// <param name="Name">Wire name, <c>category_verb_object</c> (e.g. <c>audio_set_volume</c>).</param>
/// <param name="Category">Category id (see <see cref="ToolCategories"/>).</param>
/// <param name="Title">Short human readable title.</param>
/// <param name="Description">Description shown to agents. State units, ranges and side effects.</param>
/// <param name="DefaultPolicy">Global permission on first install.</param>
/// <param name="ReadOnly">
/// True when the tool never changes system state. Read-only tools run without resource locks and without a
/// pre-execution audit record. This flag is also exported as an MCP hint but is never used for authorization.
/// </param>
/// <param name="AuditedArguments">
/// Allow-list of argument names that may be written to the audit log. Anything else is never logged.
/// </param>
/// <param name="Timeout">Upper bound for one execution. Defaults to <see cref="ToolDescriptor.DefaultTimeout"/>.</param>
/// <param name="LocalOnly">
/// True for local recovery actions of the console (for example restoring a display mode). Such a tool is not an MCP
/// tool: MCP clients can neither list nor call it (it is reported as unknown), it has no global or per-client
/// permission, and it is not shown in the tool and client pages. It still runs through <see cref="ToolExecutor"/>
/// (validation, locks, timeout, audit).
/// </param>
/// <param name="Transition">
/// Set only for PC-wide power transitions (lock, sleep, shutdown, restart). The executor then admits the call exclusively
/// through <see cref="PowerTransitionGate"/>: only when no other state change is admitted, and every new state change is
/// refused while it runs or is held. Never derived from client input.
/// </param>
public sealed record ToolDescriptor(
    string Name,
    string Category,
    string Title,
    string Description,
    DefaultPolicy DefaultPolicy,
    bool ReadOnly,
    IReadOnlyList<string> AuditedArguments,
    TimeSpan? Timeout = null,
    bool LocalOnly = false,
    PowerTransition? Transition = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveTimeout => Timeout ?? DefaultTimeout;
}

/// <summary>Category ids. Keep in sync with spec §2 Q7.</summary>
public static class ToolCategories
{
    public const string Audio = "audio";
    public const string Display = "display";
    public const string Media = "media";
    public const string Power = "power";
    public const string System = "system";
    public const string Apps = "apps";
    public const string Network = "network";
    public const string Bluetooth = "bluetooth";
    public const string Notification = "notification";
}
