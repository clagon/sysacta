using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Sysacta.Core.Security;

/// <summary>Bearer token generation and hashing. Token text is never stored or logged; only <see cref="Hash"/> is persisted.</summary>
public static class ClientTokens
{
    public const string Prefix = "sya_";
    private const int EntropyBytes = 32; // 256 bit
    public static readonly int TokenLength = Prefix.Length + Base64Url.GetEncodedLength(EntropyBytes);

    /// <summary>Creates a new random token and its hash.</summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(EntropyBytes));
        return (token, Hash(token));
    }

    /// <summary>SHA-256 of the token's UTF-8 bytes as lowercase hex.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Cheap shape check before hashing (length/prefix/alphabet). Not a security decision by itself.</summary>
    public static bool HasValidShape(string token) =>
        token.Length == TokenLength
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && Base64Url.IsValid(token.AsSpan(Prefix.Length));
}

public enum AuthFailure
{
    MissingCredentials,
    Malformed,
    UnknownToken,
    Revoked,
    Expired,
}

public sealed record AuthResult(ClientRecord? Client, AuthFailure? Failure)
{
    public bool Succeeded => Client is not null;
}

/// <summary>Validates an <c>Authorization</c> header value against stored token hashes.</summary>
public sealed class TokenAuthenticator(IAccessStore store, TimeProvider time)
{
    public AuthResult Authenticate(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return new(null, AuthFailure.MissingCredentials);
        }

        const string scheme = "Bearer ";
        if (!authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, AuthFailure.Malformed);
        }

        var token = authorizationHeader[scheme.Length..].Trim();
        if (!ClientTokens.HasValidShape(token))
        {
            return new(null, AuthFailure.Malformed);
        }

        var hash = ClientTokens.Hash(token);
        if (store.FindClientByTokenHash(hash) is not { } found
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(found.TokenHash)))
        {
            return new(null, AuthFailure.UnknownToken);
        }

        var now = time.GetUtcNow();
        if (found.Client.IsRevoked)
        {
            return new(null, AuthFailure.Revoked);
        }

        if (found.Client.IsExpired(now))
        {
            return new(null, AuthFailure.Expired);
        }

        store.RecordClientUse(found.Client.Id, now);
        return new(found.Client, null);
    }
}
