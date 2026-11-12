using System.Security.Cryptography;

namespace Mango.Peers;

internal static class PeerCredentials
{
    private const int PasswordRounds = 600_000;
    public const int SecretLength = 64;

    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool IsSecret(string? value) => value is { Length: SecretLength } && value.All(char.IsAsciiHexDigit);
    public static string Digest(string secret) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(secret)));

    public static bool Verify(string? secret, string? expected) => IsSecret(secret) && IsSecret(expected) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Convert.FromHexString(secret!)), Convert.FromHexString(expected!));

    public static AuthorityRecord Password(string? password, string recoveryVerifier, Revision revision)
    {
        if (password is not { Length: >= 12 and <= 256 } || string.IsNullOrWhiteSpace(password))
            throw new PeerFailure(400, "Enter a master password of 12 to 256 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(revision, Convert.ToHexString(salt), Convert.ToHexString(Derive(password, salt)), recoveryVerifier);
    }

    public static bool VerifyPassword(string? password, AuthorityRecord authority) =>
        password is { Length: > 0 and <= 256 } &&
        CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromHexString(authority.Salt)),
            Convert.FromHexString(authority.PasswordVerifier));

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordRounds, HashAlgorithmName.SHA256, 32);

    public static string Key(Guid id, string secret) => $"mango_{id:N}.{secret}";

    public static bool TryParseKey(string? token, out Guid id, out string secret)
    {
        id = Guid.Empty;
        secret = "";
        if (token is not { Length: 103 } || !token.StartsWith("mango_", StringComparison.Ordinal) || token[38] != '.' ||
            !Guid.TryParseExact(token.AsSpan(6, 32), "N", out id) || id == Guid.Empty || !IsSecret(token[39..]))
            return false;
        secret = token[39..];
        return true;
    }

    public static string Recovery(Guid network, string secret) => $"mango-recovery.{network:N}.{secret}";

    public static bool VerifyRecovery(string? record, Guid network, AuthorityRecord authority)
    {
        var prefix = $"mango-recovery.{network:N}.";
        return record?.Length == prefix.Length + SecretLength && record.StartsWith(prefix, StringComparison.Ordinal) &&
            Verify(record[prefix.Length..], authority.RecoveryVerifier);
    }

    public static string? Bearer(HttpRequest request)
    {
        var value = request.Headers.Authorization;
        return value.Count == 1 && value[0] is { Length: > 7 and <= 128 } header &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }
}
