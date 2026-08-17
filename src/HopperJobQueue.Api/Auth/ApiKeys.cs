using System.Security.Cryptography;
using System.Text;

namespace HopperJobQueue.Api.Auth;

/// <summary>
/// Key format: <c>hjq_{scope}_{32 base62 characters}</c>.
/// The clear-text stored prefix is 16 characters (not 12): "hjq_producer" is already
/// 12 characters on its own, so two producer keys would share an identical prefix and the
/// table's unique constraint could never hold. 16 characters keep 3 to 6 characters of
/// random discriminant depending on the scope, without revealing anything useful about
/// the secret (at most ~36 bits out of 190 bits of entropy).
/// </summary>
public static class ApiKeys
{
    public const int PrefixLength = 16;
    private const int SecretLength = 32;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Generate(string scope)
    {
        var secret = new char[SecretLength];
        for (var i = 0; i < secret.Length; i++)
        {
            secret[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"hjq_{scope}_{new string(secret)}";
    }

    public static string Prefix(string key) => key.Length <= PrefixLength ? key : key[..PrefixLength];

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Constant-time comparison of the presented key's SHA-256 hash.</summary>
    public static bool Verify(byte[] storedHash, string presentedKey) =>
        CryptographicOperations.FixedTimeEquals(storedHash, Hash(presentedKey));

    public static bool HasValidShape(string key, string? scope = null)
    {
        var scopes = scope is null ? Domain.ApiScope.All : [scope];
        foreach (var s in scopes)
        {
            var prefix = $"hjq_{s}_";
            if (key.Length == prefix.Length + SecretLength
                && key.StartsWith(prefix, StringComparison.Ordinal)
                && key.AsSpan(prefix.Length).IndexOfAnyExcept(Alphabet.AsSpan()) < 0)
            {
                return true;
            }
        }

        return false;
    }
}
