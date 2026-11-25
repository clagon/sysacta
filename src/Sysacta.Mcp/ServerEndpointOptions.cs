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

    /// <summary>Per client token (spec §10 provisional value).</summary>
    public int RequestsPerMinutePerClient { get; init; } = 60;

    /// <summary>
    /// Rejected requests (authentication, Host, Origin) audited per remote address per minute. Authentication failures
    /// beyond this get 429 without an audit record; Host/Origin rejections stay 403 but are no longer recorded.
    /// </summary>
    public int AuthFailuresPerMinutePerAddress { get; init; } = 30;

    public int MaxConcurrentConnections { get; init; } = 64;

    public bool IsLoopbackOnly => IPAddress.IsLoopback(BindAddress);
}
