using System.Net;
using System.Security.Claims;
using System.Text.Json;
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
    public const string CredentialClaim = "sysacta:credential";
    public const string AuthenticationType = "SysactaBearer";

    private readonly HashSet<string> _allowedHosts = BuildAllowedHosts(options);
    private readonly HashSet<string> _allowedOrigins = new(options.AllowedOrigins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

    private readonly PartitionedRateLimiter<string> _rejectionLimiter = PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.AuthFailuresPerMinutePerAddress,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString();

        if (!IsHostAllowed(context.Request.Host))
        {
            Audit(context, remote, AuditOutcome.Denied, "HostNotAllowed", ("host", context.Request.Host.Value));
            await RejectAsync(context, StatusCodes.Status403Forbidden,
                "Host not allowed. Add this host name to the allowed host names in the Sysacta console (Connection).");
            return;
        }

        if (context.Request.Headers.Origin is { Count: > 0 } origin && !_allowedOrigins.Contains(origin.ToString().TrimEnd('/')))
        {
            Audit(context, remote, AuditOutcome.Denied, "OriginNotAllowed", ("origin", origin.ToString()));
            await RejectAsync(context, StatusCodes.Status403Forbidden,
                "Origin not allowed. Add this origin to the allowed origins in the Sysacta console (Connection).");
            return;
        }

        var result = authenticator.Authenticate(context.Request.Headers.Authorization.Count == 1 ? context.Request.Headers.Authorization.ToString() : null);
        if (!result.Succeeded)
        {
            if (!Audit(context, remote, AuditOutcome.AuthFailed, result.Failure.ToString()!))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var client = result.Client!;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClientIdClaim, client.Id), new Claim(ClaimTypes.Name, client.Name), new Claim(RemoteAddressClaim, remote ?? ""), new Claim(CredentialClaim, result.TokenHash!)],
            AuthenticationType));

        await next(context);
    }

    /// <summary>
    /// Records a rejected request (Host/Origin/authentication) with its method and path, and the offending Host/Origin
    /// value if any. Never records the query string, headers like Authorization, or the body. Rejections are recorded at
    /// most <see cref="ServerEndpointOptions.AuthFailuresPerMinutePerAddress"/> times per minute per address (behind a
    /// reverse proxy all requests share 127.0.0.1); returns false when that limit is exceeded.
    /// </summary>
    private bool Audit(HttpContext context, string? remote, AuditOutcome outcome, string code, (string Name, string? Value) detail = default)
    {
        using var lease = _rejectionLimiter.AttemptAcquire(remote ?? "unknown");
        if (!lease.IsAcquired)
        {
            return false;
        }

        var request = new Dictionary<string, string?>
        {
            ["method"] = context.Request.Method,
            ["path"] = Truncate(context.Request.Path.Value, 200),
        };
        if (detail.Name is not null)
        {
            request[detail.Name] = Truncate(detail.Value, 200);
        }

        try
        {
            audit.Write(new AuditEvent
            {
                RequestId = Guid.CreateVersion7().ToString("N"),
                Timestamp = time.GetUtcNow(),
                Source = "http",
                Outcome = outcome,
                ErrorCode = code,
                Arguments = JsonSerializer.Serialize(request),
                RemoteAddress = remote,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The request is rejected either way; an audit outage must not turn 401/403 into 500.
        }

        return true;
    }

    /// <summary>403 with a short, fixed plain-text reason (never echoes request values).</summary>
    private static Task RejectAsync(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return context.Response.WriteAsync(reason);
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;

    /// <summary>Recreates the caller from the authenticated principal. Throws if the request was not authenticated.</summary>
    public static Caller CallerFrom(ClaimsPrincipal? user)
    {
        var id = user?.FindFirst(ClientIdClaim)?.Value;
        if (user?.Identity?.AuthenticationType != AuthenticationType || string.IsNullOrEmpty(id))
        {
            throw new UnauthorizedAccessException("The request is not authenticated.");
        }

        var remote = user.FindFirst(RemoteAddressClaim)?.Value;
        var credential = user.FindFirst(CredentialClaim)?.Value ?? throw new UnauthorizedAccessException("The request is not authenticated.");
        return Caller.Client(id, user.FindFirst(ClaimTypes.Name)?.Value ?? id, string.IsNullOrEmpty(remote) ? null : remote, credential);
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
