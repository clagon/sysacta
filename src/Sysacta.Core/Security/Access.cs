using Sysacta.Core.Tools;

namespace Sysacta.Core.Security;

public enum CallerKind
{
    /// <summary>An MCP client authenticated with a Bearer token.</summary>
    McpClient,
    /// <summary>The local management console's "test" action. Subject to global permission, validation and audit.</summary>
    LocalConsole,
}

/// <summary>Who is calling. Built only by the authentication layer (MCP) or the console.</summary>
public sealed record Caller(CallerKind Kind, string? ClientId, string DisplayName, string? RemoteAddress)
{
    public static Caller LocalConsole { get; } = new(CallerKind.LocalConsole, null, "Local console", null);

    public static Caller Client(string clientId, string name, string? remoteAddress) =>
        new(CallerKind.McpClient, clientId, name, remoteAddress);

    public string Source => Kind == CallerKind.McpClient ? "mcp" : "local_console";
}

/// <summary>A registered MCP client (never contains the token).</summary>
public sealed record ClientRecord(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt)
{
    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);
}

/// <summary>
/// Read side of the permission data, as seen by the request pipeline.
/// Deliberately has no write operations for permissions or clients: the MCP layer only receives this interface,
/// so it has no way to change authentication or authorization settings (spec §6).
/// Implementations must read the current persisted state on every call (no caching), so changes apply immediately.
/// </summary>
public interface IAccessStore
{
    bool IsToolEnabled(string toolName);

    ClientRecord? GetClient(string clientId);

    /// <summary>Looks up a client by SHA-256 token hash (lowercase hex). Returns the stored hash for constant-time comparison.</summary>
    (ClientRecord Client, string TokenHash)? FindClientByTokenHash(string tokenHash);

    bool IsClientToolAllowed(string clientId, string toolName);

    void RecordClientUse(string clientId, DateTimeOffset at);
}

public enum AccessDecision
{
    Allowed,
    NotAvailable,
    ToolDisabled,
    ClientInactive,
    NotPermitted,
}

/// <summary>Effective permission = global ∩ client ∩ capability (spec §6).</summary>
public sealed class AccessPolicy(IAccessStore store, TimeProvider time)
{
    public AccessDecision Evaluate(Caller caller, SysactaTool tool)
    {
        if (!tool.GetAvailability().IsAvailable)
        {
            return AccessDecision.NotAvailable;
        }

        if (!store.IsToolEnabled(tool.Name))
        {
            return AccessDecision.ToolDisabled;
        }

        if (caller.Kind == CallerKind.LocalConsole)
        {
            return AccessDecision.Allowed;
        }

        if (caller.ClientId is null || store.GetClient(caller.ClientId) is not { } client || !client.IsActive(time.GetUtcNow()))
        {
            return AccessDecision.ClientInactive;
        }

        return store.IsClientToolAllowed(client.Id, tool.Name) ? AccessDecision.Allowed : AccessDecision.NotPermitted;
    }

    internal static ToolResult ToResult(AccessDecision decision, SysactaTool tool) => decision switch
    {
        AccessDecision.NotAvailable => NotAvailable(tool.GetAvailability()),
        AccessDecision.ToolDisabled => ToolResult.Fail(ErrorCodes.ToolDisabled, "The tool is disabled on this server."),
        AccessDecision.ClientInactive => ToolResult.Fail(ErrorCodes.PermissionDenied, "The client credentials are no longer valid."),
        AccessDecision.NotPermitted => ToolResult.Fail(ErrorCodes.PermissionDenied, "This client is not permitted to use the tool."),
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };

    private static ToolResult NotAvailable(ToolAvailability availability) =>
        ToolResult.Fail(availability.ErrorCode ?? ErrorCodes.NotSupported, availability.Reason ?? "The tool is not supported in this environment.");
}

/// <summary>Permission presets offered when a client is issued (spec §6).</summary>
public enum PermissionPreset
{
    /// <summary>All tools that are globally enabled at issue time.</summary>
    AllEnabled,
    /// <summary>Only globally enabled read-only tools.</summary>
    ReadOnly,
    /// <summary>Nothing; grant tools individually.</summary>
    None,
}

public static class PermissionPresets
{
    /// <summary>
    /// Initial per-client grants. Tools added later are never granted automatically, because grants are stored
    /// as explicit rows and absence means deny.
    /// </summary>
    public static IReadOnlyList<string> InitialGrants(PermissionPreset preset, IEnumerable<SysactaTool> tools, Func<string, bool> isGloballyEnabled) =>
        preset switch
        {
            PermissionPreset.AllEnabled => [.. tools.Where(t => isGloballyEnabled(t.Name)).Select(t => t.Name)],
            PermissionPreset.ReadOnly => [.. tools.Where(t => t.Descriptor.ReadOnly && isGloballyEnabled(t.Name)).Select(t => t.Name)],
            _ => [],
        };
}
