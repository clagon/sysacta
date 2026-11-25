using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;

namespace Sysacta.Mcp;

/// <summary>
/// HTTP-level security applied to every request before the MCP SDK sees it:
/// Host/Origin validation (DNS rebinding), Bearer authentication and authentication-failure auditing.
/// </summary>
internal sealed class RequestGuards(ServerEndpointOptions options, TokenAuthenticator authenticator, IAuditLog audit, TimeProvider time)
{
    public const string ClientIdClaim = "sysacta:client_id";
    public const string RemoteAddressClaim = "sysacta:remote";
    public const string AuthenticationType = "SysactaBearer";

    private readonly HashSet<string> _allowedHosts = BuildAllowedHosts(options);
    private readonly HashSet<string> _allowedOrigins = new(options.AllowedOrigins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

    private readonly PartitionedRateLimiter<string> _authFailureLimiter = PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.AuthFailuresPerMinutePerAddress,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsHostAllowed(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (context.Request.Headers.Origin is { Count: > 0 } origin && !_allowedOrigins.Contains(origin.ToString().TrimEnd('/')))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var remote = context.Connection.RemoteIpAddress?.ToString();
        var result = authenticator.Authenticate(context.Request.Headers.Authorization.Count == 1 ? context.Request.Headers.Authorization.ToString() : null);
        if (!result.Succeeded)
        {
            using var lease = _authFailureLimiter.AttemptAcquire(remote ?? "unknown");
            if (!lease.IsAcquired)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            try
            {
                // Never record the token or header. Unknown clients are recorded with a null client id.
                audit.Write(new AuditEvent
                {
                    RequestId = Guid.CreateVersion7().ToString("N"),
                    Timestamp = time.GetUtcNow(),
                    Source = "http",
                    Outcome = AuditOutcome.AuthFailed,
                    ErrorCode = result.Failure.ToString(),
                    RemoteAddress = remote,
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The request is rejected either way; an audit outage must not turn 401 into 500.
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        var client = result.Client!;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClientIdClaim, client.Id), new Claim(ClaimTypes.Name, client.Name), new Claim(RemoteAddressClaim, remote ?? "")],
            AuthenticationType));

        await next(context);
    }

    /// <summary>Recreates the caller from the authenticated principal. Throws if the request was not authenticated.</summary>
    public static Caller CallerFrom(ClaimsPrincipal? user)
    {
        var id = user?.FindFirst(ClientIdClaim)?.Value;
        if (user?.Identity?.AuthenticationType != AuthenticationType || string.IsNullOrEmpty(id))
        {
            throw new UnauthorizedAccessException("The request is not authenticated.");
        }

        var remote = user.FindFirst(RemoteAddressClaim)?.Value;
        return Caller.Client(id, user.FindFirst(ClaimTypes.Name)?.Value ?? id, string.IsNullOrEmpty(remote) ? null : remote);
    }

    internal bool IsHostAllowed(HostString host)
    {
        if (!host.HasValue)
        {
            return false;
        }

        var name = host.Host.Trim('[', ']');
        if (_allowedHosts.Contains(name))
        {
            return true;
        }

        // IP literals are not subject to DNS rebinding. When listening on all interfaces, accept any of them.
        return IPAddress.TryParse(name, out var ip)
            && (IPAddress.IsLoopback(ip) || options.BindAddress.Equals(IPAddress.Any) || options.BindAddress.Equals(IPAddress.IPv6Any) || ip.Equals(options.BindAddress));
    }

    private static HashSet<string> BuildAllowedHosts(ServerEndpointOptions options)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        foreach (var host in options.AllowedHosts)
        {
            // Accept "name" or "name:port"; the port is not part of the check.
            var value = host.Trim();
            var colon = value.LastIndexOf(':');
            hosts.Add(colon > 0 && !value.Contains(']', StringComparison.Ordinal) && value.Count(c => c == ':') == 1 ? value[..colon] : value.Trim('[', ']'));
        }

        return hosts;
    }
}
