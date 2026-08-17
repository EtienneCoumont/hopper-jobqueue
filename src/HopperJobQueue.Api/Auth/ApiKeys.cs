using System.Security.Cryptography;
using System.Text;

namespace HopperJobQueue.Api.Auth;

/// <summary>
/// Format de clé : <c>hjq_{scope}_{32 caractères base62}</c>.
/// Le préfixe stocké en clair fait 16 caractères (et non 12) : « hjq_producer » fait déjà
/// 12 caractères à lui seul, deux clés producer auraient donc un préfixe identique et la
/// contrainte d'unicité de la table serait inviolable. 16 caractères gardent 3 à 6 caractères
/// de discriminant aléatoire selon le scope, sans rien révéler d'utile du secret (au plus
/// ~36 bits sur les 190 bits d'entropie).
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

    /// <summary>Comparaison en temps constant du hash SHA-256 de la clé présentée.</summary>
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
