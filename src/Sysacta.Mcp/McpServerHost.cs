using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools;

namespace Sysacta.Mcp;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
}

/// <summary>
/// Owns the Kestrel/MCP host. Start/stop/apply are serialized. The <see cref="ToolExecutor"/> (and its locks) outlives
/// host restarts, so restarting never releases a lock held by a still-running native call.
/// Only <c>/mcp</c> is mapped (Streamable HTTP, stateless); no stdio or legacy SSE endpoints.
/// </summary>
public sealed class McpServerHost(ToolExecutor executor, TokenAuthenticator authenticator, IAuditLog audit, TimeProvider time, string serverVersion) : IAsyncDisposable
{
    public const string EndpointPath = "/mcp";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private int _activeRequests;

    public event EventHandler? StateChanged;

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>User-facing reason of the last failure (no exception details).</summary>
    public string? LastError { get; private set; }

    public ServerEndpointOptions? Options { get; private set; }

    /// <summary>Actual bound port (differs from options when 0 was requested).</summary>
    public int? BoundPort { get; private set; }

    /// <summary>HTTP requests currently being processed (stateless MCP has no notion of "connected agents").</summary>
    public int ActiveRequests => Volatile.Read(ref _activeRequests);

    public async Task StartAsync(ServerEndpointOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(ServerState.Stopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restarts with new options. If they fail to bind, the previous options are restored; if that also fails the
    /// host stays stopped. Returns null on success, otherwise a user-facing error.
    /// </summary>
    public async Task<string?> ApplyAsync(ServerEndpointOptions options)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = State == ServerState.Running ? Options : null;
            await StopCoreAsync().ConfigureAwait(false);
            try
            {
                await StartCoreAsync(options, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (Exception) when (previous is not null)
            {
                var error = LastError;
                try
                {
                    await StartCoreAsync(previous, CancellationToken.None).ConfigureAwait(false);
                    LastError = error + " The previous settings were restored.";
                }
                catch (Exception)
                {
                    LastError = error + " Restoring the previous settings also failed; the server is stopped.";
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                return LastError;
            }
            catch (Exception)
            {
                return LastError;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StartCoreAsync(ServerEndpointOptions options, CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return;
        }

        SetState(ServerState.Starting, null);
        var app = Build(options);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            SetState(ServerState.Faulted, ex is IOException
                ? $"Could not listen on {options.BindAddress}:{options.Port}. The port may be in use or the address is not available."
                : "The server failed to start.");
            throw;
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BoundPort = new Uri(address).Port;
        Options = options with { Port = BoundPort.Value }; // so a restore after a failed apply reuses the same port

        _app = app;
        SetState(ServerState.Running, null);
    }

    private async Task StopCoreAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        SetState(ServerState.Stopping, null);
        _app = null;
        BoundPort = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetState(ServerState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private WebApplication Build(ServerEndpointOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "Sysacta", ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            kestrel.Limits.MaxConcurrentConnections = options.MaxConcurrentConnections;
            kestrel.Listen(options.BindAddress, options.Port);
        });

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst(RequestGuards.ClientIdClaim)?.Value ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = options.RequestsPerMinutePerClient, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation { Name = "sysacta", Title = "Sysacta", Version = serverVersion };
                server.ServerInstructions = "Sysacta exposes structured Windows PC management tools. Results use status/verification fields; treat status 'unknown' as possibly applied.";
            })
            .WithHttpTransport(http =>
            {
                http.SessionMode = HttpServerSessionMode.Stateless; // legacy SSE stays disabled (SDK default)
            })
            .WithListToolsHandler((context, cancellationToken) =>
            {
                var caller = RequestGuards.CallerFrom(context.User);
                try
                {
                    return ValueTask.FromResult(new ListToolsResult { Tools = [.. executor.ListTools(caller).Select(ToProtocolTool)] });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Do not leak exception details (paths, SQL) to the client.
                    throw new McpProtocolException("Internal error.", McpErrorCode.InternalError);
                }
            })
            .WithCallToolHandler(async (context, cancellationToken) =>
            {
                var caller = RequestGuards.CallerFrom(context.User);
                var request = context.Params ?? throw new McpProtocolException("Missing parameters.", McpErrorCode.InvalidParams);
                JsonElement? arguments = request.Arguments is { } args ? JsonSerializer.SerializeToElement(args) : null;
                var result = await executor.ExecuteAsync(caller, request.Name, arguments, cancellationToken).ConfigureAwait(false);
                if (result.Error?.Code == ToolExecutor.UnknownToolCode)
                {
                    throw new McpProtocolException("Unknown tool.", McpErrorCode.InvalidParams);
                }

                var json = JsonSerializer.SerializeToElement(result, ToolJson.Options);
                return new CallToolResult
                {
                    StructuredContent = json,
                    Content = [new TextContentBlock { Text = json.GetRawText() }],
                    IsError = result.Status != ToolStatus.Success,
                };
            });

        var guards = new RequestGuards(options, authenticator, audit, time);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _activeRequests);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        });
        app.Use(guards.InvokeAsync);
        app.UseRateLimiter();
        app.MapMcp(EndpointPath);
        return app;
    }

    private static Tool ToProtocolTool(SysactaTool tool) => new()
    {
        Name = tool.Name,
        Title = tool.Descriptor.Title,
        Description = tool.Descriptor.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        // Hints only; never used for authorization.
        Annotations = new ToolAnnotations
        {
            Title = tool.Descriptor.Title,
            ReadOnlyHint = tool.Descriptor.ReadOnly,
            DestructiveHint = !tool.Descriptor.ReadOnly && tool.Descriptor.DefaultPolicy != DefaultPolicy.On,
            OpenWorldHint = false,
        },
    };
}
