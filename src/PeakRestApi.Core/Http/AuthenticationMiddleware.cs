// Adapted from https://github.com/nglmercer/REPOapi (MIT). See third-party/REPOapi-NOTICE.md.
using System;
using System.Security.Cryptography;
using System.Text;

namespace PeakRestApi.Core.Http;

/// <summary>
/// Bearer token generation and comparison.
/// </summary>
public static class AuthenticationMiddleware
{
    /// <summary>
    /// Generates a cryptographically secure 32 byte token encoded as Base64.
    /// </summary>
    public static string GenerateToken()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Compares the presented bearer token with the configured token in
    /// constant time with respect to the token contents.
    /// </summary>
    public static bool Authenticate(string? authorization, string token)
    {
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = authorization.Substring(7);
        if (presented.Length == 0 || token.Length == 0)
        {
            return false;
        }

        using var sha = SHA256.Create();
        var a = sha.ComputeHash(Encoding.UTF8.GetBytes(presented));
        var b = sha.ComputeHash(Encoding.UTF8.GetBytes(token));
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    /// <summary>
    /// Removes a token from a string so it can be written to a log.
    /// </summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 8 ? "***" : value.Substring(0, 4) + "***" + value.Substring(value.Length - 4);
    }
}
