using System.Net;
using Sysacta.Core.Auditing;
using Sysacta.Infrastructure;

namespace Sysacta.Mcp.Tests;

/// <summary>Tests that change process-wide environment variables; never run in parallel with others.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// SEC-08: the embedded host must not pick up ASP.NET Core settings from the environment or appsettings:
/// no extra listeners, no forwarded-header rewriting of the client address, no development behavior.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public class HostConfigurationTests
{
    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Ambient_aspnetcore_configuration_is_ignored()
    {
        var extraPort = FreePort();
        var urlsPort = FreePort();
        var variables = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{urlsPort}",
            ["Kestrel__Endpoints__Extra__Url"] = $"http://127.0.0.1:{extraPort}",
        };
        var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (k, v) in variables)
            {
                Environment.SetEnvironmentVariable(k, v);
            }

            await using var f = await ServerFixture.StartAsync();

            Assert.True(Wait.IsPortFree(extraPort), "Kestrel endpoint from configuration must not be added.");
            Assert.True(Wait.IsPortFree(urlsPort), "ASPNETCORE_URLS must not add a listener.");

            using var http = f.Http("sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            using var request = ServerFixture.JsonRpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("127.0.0.1", Assert.Single(f.Audit.Query(new AuditQuery(Outcome: AuditOutcome.AuthFailed))).RemoteAddress);
        }
        finally
        {
            foreach (var (k, v) in previous)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }
}
