using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;

namespace Sysacta.Mcp;

/// <summary>
/// HTTP-level security applied to every request before the MCP SDK sees it, in this order (SEC-05):
/// <list type="number">
/// <item>request budget, total and per socket address (no database access before it);</item>
/// <item>a bounded number of requests in the following stage at once;</item>
/// <item>Host/Origin validation (DNS rebinding), Bearer authentication, rejection auditing;</item>
/// <item>per-client budget, and only then the last-used time.</item>
/// </list>
/// Rejections beyond the audit budget are counted in memory and written as at most one aggregated record per reason
/// and minute (SEC-06). One instance belongs to one run of the host; its counters are discarded with it.
/// </summary>
internal sealed partial class RequestGuards(ServerEndpointOptions options, TokenAuthenticator authenticator, IAuditLog audit, TimeProvider time)
{
    public const string ClientIdClaim = "sysacta:client_id";
    public const string RemoteAddressClaim = "sysacta:remote";
    public const string CredentialClaim = "sysacta:credential";
    public const string AuthenticationType = "SysactaBearer";

    /// <summary>Reason codes of rejections that are never recorded individually.</summary>
    public const string RequestBudgetExceeded = "RequestBudgetExceeded";
    public const string AuthenticationBusy = "AuthenticationBusy";
    public const string ClientRateLimited = "ClientRateLimited";

    private readonly HashSet<string> _allowedHosts = BuildAllowedHosts(options);
    private readonly HashSet<string> _allowedOrigins = new(options.AllowedOrigins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

    // Never disposed: it has no wait handle, and a request finishing after the host stopped may still release it.
    private readonly SemaphoreSlim _authenticationSlots = new(options.MaxConcurrentAuthentications, options.MaxConcurrentAuthentications);

    /// <summary>Requests before authentication, per socket address and in total.</summary>
    internal RequestBudget RequestBudget { get; } = new(options.RequestsPerMinutePerAddress, options.RequestsPerMinute, time);

    /// <summary>Authenticated requests per client. Keys are client IDs that passed authentication, so they are bounded by the registered clients.</summary>
    internal RequestBudget ClientBudget { get; } = new(options.RequestsPerMinutePerClient, int.MaxValue, time);

    /// <summary>Individually audited rejections, per socket address and in total.</summary>
    internal RequestBudget RejectionAuditBudget { get; } = new(options.AuthFailuresPerMinutePerAddress, options.AuditedRejectionsPerMinute, time);

    private readonly Lock _aggregateLock = new();
    // Keyed by a fixed reason code (never client text or an address), so it has at most a dozen entries.
    private readonly Dictionary<string, (AuditOutcome Outcome, long Count)> _aggregates = new(StringComparer.Ordinal);
    private DateTimeOffset? _aggregateStart;

    // Background writes of counted rejections, chained so they run one at a time and can be awaited on stop.
    private Task _aggregateWrites = Task.CompletedTask;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var remote = NormalizeAddress(context.Connection.RemoteIpAddress);

        if (!RequestBudget.TryAcquire(remote ?? "unknown", out var retryAfter))
        {
            CountRejection(RequestBudgetExceeded, AuditOutcome.Denied);
            TooManyRequests(context, retryAfter);
            return;
        }

        FlushAggregatesInBackground();

        // The slot covers only the checks and the token lookup (one read). Database writes (rejection records, the
        // last-used time) happen after it is returned, so a database busy with maintenance cannot fill every slot
        // with waiting writes and lock out valid clients.
        if (!_authenticationSlots.Wait(0))
        {
            CountRejection(AuthenticationBusy, AuditOutcome.Denied);
            TooManyRequests(context, TimeSpan.FromSeconds(1));
            return;
        }

        string? rejection;
        AuthResult? result = null;
        try
        {
            rejection = !IsHostAllowed(context.Request.Host) ? "HostNotAllowed"
                : context.Request.Headers.Origin is { Count: > 0 } origin && !_allowedOrigins.Contains(origin.ToString().TrimEnd('/')) ? "OriginNotAllowed"
                : null;
            if (rejection is null)
            {
                result = authenticator.Authenticate(context.Request.Headers.Authorization.Count == 1 ? context.Request.Headers.Authorization.ToString() : null);
            }
        }
        finally
        {
            _authenticationSlots.Release();
        }

        if (rejection is not null)
        {
            Audit(context, remote, AuditOutcome.Denied, rejection);
            await RejectAsync(context, StatusCodes.Status403Forbidden, rejection == "HostNotAllowed"
                ? "Host not allowed. Add this host name to the allowed host names in the Sysacta console (Connection)."
                : "Origin not allowed. Add this origin to the allowed origins in the Sysacta console (Connection).");
            return;
        }

        if (!result!.Succeeded)
        {
            if (!Audit(context, remote, AuditOutcome.AuthFailed, result.Failure.ToString()!))
            {
                TooManyRequests(context, retryAfter: null);
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var client = result.Client!;
        if (!ClientBudget.TryAcquire(client.Id, out retryAfter))
        {
            CountRejection(ClientRateLimited, AuditOutcome.Denied);
            TooManyRequests(context, retryAfter);
            return;
        }

        try
        {
            authenticator.RecordUse(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The last-used time is informational; a busy database must not fail an authenticated request.
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClientIdClaim, client.Id), new Claim(ClaimTypes.Name, client.Name), new Claim(RemoteAddressClaim, remote ?? ""), new Claim(CredentialClaim, result.TokenHash!)],
            AuthenticationType));

        await next(context);
    }

    /// <summary>
    /// The socket peer address used for budgets and the audit log. IPv4-mapped IPv6 addresses become IPv4 and the IPv6
    /// scope is dropped, so one peer has one key. Forwarded headers are never consulted.
    /// </summary>
    internal static string? NormalizeAddress(IPAddress? address) => address switch
    {
        null => null,
        { IsIPv4MappedToIPv6: true } => address.MapToIPv4().ToString(),
        { AddressFamily: System.Net.Sockets.AddressFamily.InterNetworkV6 } => new IPAddress(address.GetAddressBytes()).ToString(),
        _ => address.ToString(),
    };

    private static void TooManyRequests(HttpContext context, TimeSpan? retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (retryAfter is { } wait)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Records a rejected request (Host/Origin/authentication) with fixed values only (SEC-03): a reason code, the
    /// method if it is a standard one, and whether the path was the MCP endpoint. Nothing the client typed (path, query,
    /// Host, Origin, headers, body) is stored, because no format check can prove such text carries no secret.
    /// Individual records are limited per address and in total (<see cref="RejectionAuditBudget"/>; behind a reverse
    /// proxy all requests share 127.0.0.1); beyond that the rejection is only counted and false is returned.
    /// </summary>
    private bool Audit(HttpContext context, string? remote, AuditOutcome outcome, string code)
    {
        if (!RejectionAuditBudget.TryAcquire(remote ?? "unknown", out _))
        {
            CountRejection(code, outcome);
            return false;
        }

        var request = new Dictionary<string, string?>
        {
            ["method"] = KnownMethods.Contains(context.Request.Method) ? context.Request.Method : "other",
            ["path"] = string.Equals(context.Request.Path.Value, McpServerHost.EndpointPath, StringComparison.OrdinalIgnoreCase) ? McpServerHost.EndpointPath : "other",
        };

        TryWrite(new AuditEvent
        {
            RequestId = Guid.CreateVersion7().ToString("N"),
            Timestamp = time.GetUtcNow(),
            Source = "http",
            Outcome = outcome,
            ErrorCode = code,
            Arguments = JsonSerializer.Serialize(request),
            RemoteAddress = remote,
        });
        return true;
    }

    /// <summary>Counts a rejection that is not recorded individually. Memory only; no database write per rejection.</summary>
    private void CountRejection(string code, AuditOutcome outcome)
    {
        lock (_aggregateLock)
        {
            _aggregateStart ??= time.GetUtcNow();
            _aggregates[code] = (outcome, _aggregates.GetValueOrDefault(code).Count + 1);
        }
    }

    /// <summary>
    /// Writes the counted rejections as one record per reason once their minute is over (or when the host stops,
    /// <paramref name="force"/>). Each record covers the minute starting at its timestamp.
    /// </summary>
    /// <summary>Starts writing the counted rejections when their minute is over, without delaying the request.</summary>
    private void FlushAggregatesInBackground()
    {
        if (TakeDueAggregates(force: false) is { } due)
        {
            lock (_aggregateLock)
            {
                _aggregateWrites = _aggregateWrites.ContinueWith(_ => WriteAggregates(due), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// When the host stops: waits for background writes already started (their counters are already taken), then
    /// writes whatever is still counted, so a normal shutdown loses no aggregated record.
    /// </summary>
    internal async Task CompleteAsync()
    {
        Task pending;
        lock (_aggregateLock)
        {
            pending = _aggregateWrites;
        }

        await pending.ConfigureAwait(false); // WriteAggregates never throws
        if (TakeDueAggregates(force: true) is { } due)
        {
            WriteAggregates(due);
        }
    }

    private (DateTimeOffset Start, List<KeyValuePair<string, (AuditOutcome Outcome, long Count)>> Batch)? TakeDueAggregates(bool force)
    {
        lock (_aggregateLock)
        {
            if (_aggregateStart is not { } start || (!force && time.GetUtcNow() - start < RequestBudget.Window))
            {
                return null;
            }

            List<KeyValuePair<string, (AuditOutcome Outcome, long Count)>> batch = [.. _aggregates];
            _aggregates.Clear();
            _aggregateStart = null;
            return (start, batch);
        }
    }

    private void WriteAggregates((DateTimeOffset Start, List<KeyValuePair<string, (AuditOutcome Outcome, long Count)>> Batch) due)
    {
        var (start, batch) = due;
        foreach (var (code, (outcome, count)) in batch)
        {
            TryWrite(new AuditEvent
            {
                RequestId = Guid.CreateVersion7().ToString("N"),
                Timestamp = start,
                Source = "http",
                Outcome = outcome,
                ErrorCode = code,
                AggregateCount = count,
            });
        }
    }

    private void TryWrite(AuditEvent auditEvent)
    {
        try
        {
            audit.Write(auditEvent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The request is rejected either way; an audit outage (or a full log) must not turn 401/403 into 500.
        }
    }

    /// <summary>403 with a short, fixed plain-text reason (never echoes request values).</summary>
    private static Task RejectAsync(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return context.Response.WriteAsync(reason);
    }

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal) { "GET", "POST", "DELETE", "PUT", "PATCH", "HEAD", "OPTIONS" };

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
