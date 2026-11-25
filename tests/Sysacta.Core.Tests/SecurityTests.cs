using Sysacta.Core.Security;
using Sysacta.Core.Tools;

namespace Sysacta.Core.Tests;

public class ClientTokenTests
{
    [Fact]
    public void Tokens_are_random_256_bit_and_hashed()
    {
        var (a, hashA) = ClientTokens.Generate();
        var (b, _) = ClientTokens.Generate();

        Assert.NotEqual(a, b);
        Assert.StartsWith(ClientTokens.Prefix, a, StringComparison.Ordinal);
        Assert.Equal(ClientTokens.TokenLength, a.Length);
        Assert.Equal(43, a.Length - ClientTokens.Prefix.Length); // base64url of 32 bytes
        Assert.True(ClientTokens.HasValidShape(a));
        Assert.Equal(64, hashA.Length);
        Assert.Equal(hashA, ClientTokens.Hash(a));
        Assert.DoesNotContain(a[4..], hashA, StringComparison.Ordinal);
    }
}

public class TokenAuthenticatorTests
{
    private readonly FakeAccessStore _store = new();
    private readonly TokenAuthenticator _auth;

    public TokenAuthenticatorTests() => _auth = new TokenAuthenticator(_store, TimeProvider.System);

    [Theory]
    [InlineData(null, AuthFailure.MissingCredentials)]
    [InlineData("", AuthFailure.MissingCredentials)]
    [InlineData("Basic dXNlcjpwYXNz", AuthFailure.Malformed)]
    [InlineData("Bearer", AuthFailure.Malformed)]
    [InlineData("Bearer short", AuthFailure.Malformed)]
    [InlineData("Bearer sya_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", AuthFailure.UnknownToken)]
    public void Rejects_missing_malformed_and_unknown(string? header, AuthFailure expected)
    {
        _store.AddClient("c1");
        var result = _auth.Authenticate(header);
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public void Accepts_valid_token_and_records_use()
    {
        var (_, token) = _store.AddClient("c1");
        var result = _auth.Authenticate("bearer " + token);
        Assert.True(result.Succeeded);
        Assert.Equal("c1", result.Client!.Id);
        Assert.Single(_store.Uses);
    }

    [Fact]
    public void Rejects_revoked_and_expired()
    {
        var (_, revoked) = _store.AddClient("r");
        _store.Revoke("r");
        var (_, expired) = _store.AddClient("e");
        _store.Clients["e"] = _store.Clients["e"] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };

        Assert.Equal(AuthFailure.Revoked, _auth.Authenticate("Bearer " + revoked).Failure);
        Assert.Equal(AuthFailure.Expired, _auth.Authenticate("Bearer " + expired).Failure);
        Assert.Empty(_store.Uses);
    }

    [Fact]
    public void Token_of_one_client_cannot_be_altered_into_another()
    {
        var (_, token) = _store.AddClient("c1");
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(AuthFailure.UnknownToken, _auth.Authenticate("Bearer " + tampered).Failure);
    }
}

public class AccessPolicyTests
{
    private readonly FakeAccessStore _store = new();
    private readonly EchoTool _tool = new();
    private readonly AccessPolicy _policy;

    public AccessPolicyTests() => _policy = new AccessPolicy(_store, TimeProvider.System);

    [Theory]
    [InlineData(false, false, AccessDecision.ToolDisabled)]
    [InlineData(false, true, AccessDecision.ToolDisabled)]
    [InlineData(true, false, AccessDecision.NotPermitted)]
    [InlineData(true, true, AccessDecision.Allowed)]
    public void Effective_permission_is_intersection_of_global_and_client(bool global, bool client, AccessDecision expected)
    {
        var (caller, _) = _store.AddClient("c1");
        _store.EnabledTools[_tool.Name] = global;
        _store.Grants[("c1", _tool.Name)] = client;

        Assert.Equal(expected, _policy.Evaluate(caller, _tool));
    }

    [Fact]
    public void Unavailable_tool_is_denied_even_if_granted()
    {
        var tool = new UnavailableTool();
        var (caller, _) = _store.AddClient("c1", tool.Name);
        Assert.Equal(AccessDecision.NotAvailable, _policy.Evaluate(caller, tool));
    }

    [Fact]
    public void Revoked_expired_or_unknown_clients_are_denied()
    {
        var (caller, _) = _store.AddClient("c1", _tool.Name);
        _store.Revoke("c1");
        Assert.Equal(AccessDecision.ClientInactive, _policy.Evaluate(caller, _tool));

        var (caller2, _) = _store.AddClient("c2", _tool.Name);
        _store.Clients["c2"] = _store.Clients["c2"] with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.Equal(AccessDecision.ClientInactive, _policy.Evaluate(caller2, _tool));

        Assert.Equal(AccessDecision.ClientInactive, _policy.Evaluate(Caller.Client("ghost", "ghost", null), _tool));
    }

    [Fact]
    public void Local_console_respects_global_permission()
    {
        _store.EnabledTools[_tool.Name] = false;
        Assert.Equal(AccessDecision.ToolDisabled, _policy.Evaluate(Caller.LocalConsole, _tool));
        _store.EnabledTools[_tool.Name] = true;
        Assert.Equal(AccessDecision.Allowed, _policy.Evaluate(Caller.LocalConsole, _tool));
    }

    [Fact]
    public void Presets_never_include_disabled_tools()
    {
        var gate = new GateTool();
        SysactaTool[] tools = [_tool, gate];
        Func<string, bool> enabled = name => name == _tool.Name || name == gate.Name;

        Assert.Equal([_tool.Name, gate.Name], PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, tools, enabled));
        Assert.Equal([_tool.Name], PermissionPresets.InitialGrants(PermissionPreset.ReadOnly, tools, enabled));
        Assert.Empty(PermissionPresets.InitialGrants(PermissionPreset.None, tools, enabled));
        Assert.Equal([_tool.Name], PermissionPresets.InitialGrants(PermissionPreset.AllEnabled, tools, n => n == _tool.Name));
    }

    private sealed class UnavailableTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("display_get_thing", ToolCategories.Display, "x", "x", DefaultPolicy.On, true, []);

        public override ToolAvailability GetAvailability() => ToolAvailability.Unavailable(ErrorCodes.NotSupported, "No display.");

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
