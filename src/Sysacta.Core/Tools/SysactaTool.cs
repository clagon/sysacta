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
/// <param name="TargetId">Concrete resolved id passed to <c>ExecuteAsync</c> (null when the tool has no target).</param>
public sealed record ToolTarget(IReadOnlyList<string> LockKeys, string? TargetId)
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

    internal abstract Task<(JsonNode? Data, Verification Verification)> RunAsync(object arguments, ToolTarget target, CancellationToken cancellationToken);

    /// <summary>Allow-listed arguments for the audit log, as JSON text (null when nothing is allowed).</summary>
    internal string? SanitizeForAudit(object arguments)
    {
        if (Descriptor.AuditedArguments.Count == 0)
        {
            return null;
        }

        var all = JsonSerializer.SerializeToNode(arguments, arguments.GetType(), ToolJson.Options) as JsonObject;
        var kept = new JsonObject();
        foreach (var name in Descriptor.AuditedArguments)
        {
            if (all is not null && all.TryGetPropertyValue(name, out var value))
            {
                kept[name] = value?.DeepClone();
            }
        }

        return kept.ToJsonString();
    }
}

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
    private readonly Lazy<JsonElement> _inputSchema = new(() => JsonSerializer.SerializeToElement(ToolJson.SchemaFor(typeof(TArgs))));
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
    /// For state changes: read the previous state, apply, read back and return <see cref="Verification.Verified"/>
    /// only if the read-back matches; return a no-op success when already in the requested state.
    /// </summary>
    protected abstract Task<ToolOutcome<TData>> ExecuteAsync(TArgs args, ToolTarget target, CancellationToken cancellationToken);

    internal sealed override object Parse(JsonElement? arguments) => ToolJson.ParseArguments<TArgs>(arguments);

    internal sealed override ValueTask<ToolTarget> ResolveAsync(object arguments, CancellationToken cancellationToken) =>
        ResolveTargetAsync((TArgs)arguments, cancellationToken);

    internal sealed override async Task<(JsonNode? Data, Verification Verification)> RunAsync(object arguments, ToolTarget target, CancellationToken cancellationToken)
    {
        var outcome = await ExecuteAsync((TArgs)arguments, target, cancellationToken).ConfigureAwait(false);
        return (JsonSerializer.SerializeToNode(outcome.Data, ToolJson.Options), outcome.Verification);
    }
}
