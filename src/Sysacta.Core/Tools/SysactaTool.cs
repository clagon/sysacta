using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sysacta.Core.Tools;

/// <summary>Whether the tool can run in this environment (spec §8). Unavailable tools are hidden from tools/list.</summary>
public sealed record ToolAvailability(bool IsAvailable, string? ErrorCode, string? Reason)
{
    public static readonly ToolAvailability Available = new(true, null, null);

    /// <summary>Permanently unsupported here (missing API/hardware class). Use <see cref="ErrorCodes.NotSupported"/> or <see cref="ErrorCodes.AdminRequired"/>.</summary>
    public static ToolAvailability Unavailable(string errorCode, string reason) => new(false, errorCode, reason);
}

/// <summary>
/// The resolved target of one call: which resources must be locked and the concrete id that will be touched.
/// Resolve aliases/defaults to a concrete id here, before locking; never fall back to another device later.
/// </summary>
/// <param name="LockKeys">Resource keys, e.g. <c>audio:device:{id}</c>. Ignored for read-only tools.</param>
/// <param name="TargetId">
/// Concrete resolved id passed to <c>ExecuteAsync</c> (null when the tool has no target). It is recorded in the audit
/// log as <c>target</c>, so it must be an identifier obtained from the OS or Sysacta's own registry, never the
/// caller's raw input.
/// </param>
/// <remarks>A tool that must carry more of what it resolved (a fixed batch, for example) derives from this record.</remarks>
public record ToolTarget(IReadOnlyList<string> LockKeys, string? TargetId)
{
    public static readonly ToolTarget None = new([], null);
}

/// <summary>
/// Non-generic base used by the pipeline. Its members are internal on purpose: tools cannot be invoked except
/// through <see cref="ToolExecutor"/>, and tool authors must derive from <see cref="SysactaTool{TArgs, TData}"/>.
/// </summary>
public abstract class SysactaTool
{
    private protected SysactaTool()
    {
    }

    public abstract ToolDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;

    /// <summary>JSON Schema of the arguments (derived from the argument class).</summary>
    public abstract JsonElement InputSchema { get; }

    /// <summary>JSON Schema of the structured result envelope.</summary>
    public abstract JsonElement OutputSchema { get; }

    /// <summary>Capability check. Must be cheap and must not throw.</summary>
    public virtual ToolAvailability GetAvailability() => ToolAvailability.Available;

    internal abstract object Parse(JsonElement? arguments);

    internal abstract ValueTask<ToolTarget> ResolveAsync(object arguments, CancellationToken cancellationToken);

    internal abstract Task<ToolRun> RunAsync(object arguments, ToolExecution execution, CancellationToken cancellationToken);

    /// <summary>Argument type, for the audit allow-list check at registration.</summary>
    internal abstract Type ArgumentsType { get; }

    /// <summary>
    /// Allow-listed arguments for the audit log, as JSON text (null when nothing is recorded). Only number, boolean and
    /// enum arguments can be listed (checked at registration), so client-chosen text never reaches the log. A resolved
    /// target id is added as <c>target</c> when given (see <see cref="ToolTarget.TargetId"/>).
    /// </summary>
    internal string? SanitizeForAudit(object arguments, string? resolvedTarget)
    {
        var all = JsonSerializer.SerializeToNode(arguments, arguments.GetType(), ToolJson.Options) as JsonObject;
        var kept = new JsonObject();
        foreach (var name in Descriptor.AuditedArguments)
        {
            if (all is not null && all.TryGetPropertyValue(name, out var value))
            {
                kept[name] = value?.DeepClone();
            }
        }

        if (resolvedTarget is not null)
        {
            kept["target"] = resolvedTarget;
        }

        return kept.Count == 0 ? null : kept.ToJsonString();
    }

    /// <summary>Throws when an audited argument is not a number, boolean or enum (text could carry secrets).</summary>
    internal void ValidateAuditedArguments()
    {
        foreach (var name in Descriptor.AuditedArguments)
        {
            var property = ArgumentsType.GetProperties().FirstOrDefault(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name) == name)
                ?? throw new ArgumentException($"Tool '{Name}' audits unknown argument '{name}'.");
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!(type.IsEnum || type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal)))
            {
                throw new ArgumentException($"Tool '{Name}' audits '{name}', which is not a number, boolean or enum. Text arguments are never audited; record the resolved target id instead.");
            }
        }
    }
}

internal sealed record ToolRun(JsonNode? Data, Verification Verification, ToolStatus Status, ToolError? Error);

/// <summary>
/// Base class for every MCP tool. Implement only the domain logic; authentication, authorization,
/// argument parsing, locking, timeouts, re-authorization and auditing are applied by <see cref="ToolExecutor"/>.
/// See docs/tools.md and the reference tools under Tools/Audio.
/// </summary>
/// <typeparam name="TArgs">Argument class. Annotate with Description/Range/StringLength; use <c>required</c> for mandatory values.</typeparam>
/// <typeparam name="TData">Result data class (serialized as snake_case).</typeparam>
public abstract class SysactaTool<TArgs, TData> : SysactaTool
    where TArgs : class
{
    private readonly Lazy<JsonElement> _inputSchema = new(() => JsonSerializer.SerializeToElement(ToolJson.SchemaFor(typeof(TArgs), forArguments: true)));
    private readonly Lazy<JsonElement> _outputSchema = new(() => JsonSerializer.SerializeToElement(ToolJson.EnvelopeSchemaFor(typeof(TData))));

    public sealed override JsonElement InputSchema => _inputSchema.Value;

    public sealed override JsonElement OutputSchema => _outputSchema.Value;

    /// <summary>
    /// Resolve aliases / defaults to a concrete target and return the lock keys.
    /// Runs before locks are taken; must not change state. Throw <see cref="ToolFailureException"/> for
    /// TARGET_NOT_FOUND / AMBIGUOUS_TARGET / DEVICE_DISCONNECTED.
    /// </summary>
    protected virtual ValueTask<ToolTarget> ResolveTargetAsync(TArgs args, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolTarget.None);

    /// <summary>
    /// Perform the operation. Runs on a thread-pool (MTA) thread while the resource locks are held.
    /// For state changes: read the previous state, return a no-op success when already in the requested state, call
    /// <see cref="ToolExecution.BeginSideEffect"/> immediately before the first OS call that changes state, apply, read
    /// back and return <see cref="Verification.Verified"/> only if the read-back matches.
    /// </summary>
    protected abstract Task<ToolOutcome<TData>> ExecuteAsync(TArgs args, ToolExecution execution, CancellationToken cancellationToken);

    internal sealed override object Parse(JsonElement? arguments) => ToolJson.ParseArguments<TArgs>(arguments);

    internal sealed override ValueTask<ToolTarget> ResolveAsync(object arguments, CancellationToken cancellationToken) =>
        ResolveTargetAsync((TArgs)arguments, cancellationToken);

    internal sealed override Type ArgumentsType => typeof(TArgs);

    protected static void ReportProgress(ToolExecution execution, TData data) =>
        execution.SetProgress(JsonSerializer.SerializeToNode(data, ToolJson.Options));

    /// <summary>
    /// Records confirmed OS acceptance so a readback timeout can return accepted without releasing locks early.
    /// Only call after the final write has started and its acceptance is known; no further writes are permitted.
    /// </summary>
    protected static void ReportAccepted(ToolExecution execution, TData data) =>
        execution.SetAcceptedProgress(JsonSerializer.SerializeToNode(data, ToolJson.Options)
            ?? throw new InvalidOperationException("Accepted progress must not be null."));

    internal sealed override async Task<ToolRun> RunAsync(object arguments, ToolExecution execution, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync((TArgs)arguments, execution, cancellationToken).ConfigureAwait(false);
        return new ToolRun(JsonSerializer.SerializeToNode(outcome.Data, ToolJson.Options), outcome.Verification, outcome.Status, outcome.Error);
    }
}
