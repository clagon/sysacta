using System.Net;

namespace Sysacta.Mcp;

/// <summary>Network and limit settings for one run of the MCP host.</summary>
public sealed record ServerEndpointOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>0 = pick a free port (tests).</summary>
    public int Port { get; init; } = 8765;

    /// <summary>Additional host names accepted in the Host header (e.g. a reverse proxy name). Case-insensitive, port ignored.</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Browser origins accepted when an Origin header is present. Empty = reject all requests that carry Origin.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public long MaxRequestBodyBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// HTTP requests per minute accepted for processing from all addresses together, counted before Host/Origin checks,
    /// authentication and any database access (SEC-05). Excess gets 429 with Retry-After.
    /// </summary>
    public int RequestsPerMinute { get; init; } = 600;

    /// <summary>
    /// Like <see cref="RequestsPerMinute"/>, per socket peer address (forwarded headers are ignored). Behind a reverse
    /// proxy all clients share one address and therefore this budget.
    /// </summary>
    public int RequestsPerMinutePerAddress { get; init; } = 240;

    /// <summary>Requests in the Host/Origin/authentication stage at the same time. Excess gets 429 at once (no queue).</summary>
    public int MaxConcurrentAuthentications { get; init; } = 8;

    /// <summary>Per authenticated client (spec §10 provisional value). The last-used time is written only within it.</summary>
    public int RequestsPerMinutePerClient { get; init; } = 60;

    /// <summary>
    /// Rejected requests (authentication, Host, Origin) audited individually per remote address per minute. Authentication
    /// failures beyond this get 429; Host/Origin rejections stay 403. Both are then only counted in an aggregated record.
    /// </summary>
    public int AuthFailuresPerMinutePerAddress { get; init; } = 30;

    /// <summary>Like <see cref="AuthFailuresPerMinutePerAddress"/>, for all addresses together (SEC-06).</summary>
    public int AuditedRejectionsPerMinute { get; init; } = 120;

    public int MaxConcurrentConnections { get; init; } = 64;

    public bool IsLoopbackOnly => IPAddress.IsLoopback(BindAddress);
}
