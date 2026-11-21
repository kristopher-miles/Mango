using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Mango.Peers;

internal static class PeerCredentials
{
    private const int PasswordRounds = 600_000;
    public const int SecretLength = 64;

    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool IsSecret(string? value) => value is { Length: SecretLength } && value.All(char.IsAsciiHexDigit);
    public static string TemporarySecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public static bool IsTemporarySecret(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    public static string Digest(string secret) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(secret)));

    public static bool Verify(string? secret, string? expected) => IsSecret(secret) && IsSecret(expected) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Convert.FromHexString(secret!)), Convert.FromHexString(expected!));

    public static AuthorityRecord Password(string? password, string recoveryVerifier, Revision revision)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(revision, Convert.ToHexString(salt), Convert.ToHexString(Derive(password!, salt)), recoveryVerifier);
    }

    public static void ValidatePassword(string? password)
    {
        if (password is not { Length: >= 12 and <= 256 } || string.IsNullOrWhiteSpace(password))
            throw new PeerFailure(400, "Enter a master password of 12 to 256 characters.");
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

internal sealed record PairingCode(Uri Target, Guid Network, string Secret, string? NetworkTag = null)
{
    private const string Prefix = "mp1_";
    private static ReadOnlySpan<byte> Ipv6Prefix => [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    public static string Tag(Guid network) => Convert.ToHexString(SHA256.HashData(network.ToByteArray()).AsSpan(0, 8));

    public static string Encode(Uri target, Guid network, string secret)
    {
        var ip = IPAddress.Parse(target.DnsSafeHost);
        var address = ip.GetAddressBytes();
        var host = address.AsSpan(address.Length == 4 ? 1 : Ipv6Prefix.Length);
        var portBytes = target.Port == PeerLimits.DefaultPort ? 0 : 2;
        var payload = new byte[host.Length + portBytes + 8 + 16];
        host.CopyTo(payload);
        if (portBytes != 0)
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(host.Length), (ushort)target.Port);
        Convert.FromHexString(Tag(network)).CopyTo(payload, host.Length + portBytes);
        Convert.FromHexString(secret).CopyTo(payload, payload.Length - 16);
        return Prefix + WebEncoders.Base64UrlEncode(payload);
    }

    public static PairingCode? Parse(string? value)
    {
        if (value?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            return ParseCompact(value);
        if (value is not { Length: <= 256 } ||
            value.Split('.') is not ["mango-pair", var address, var id, var secret] ||
            address.Length == 0 || !Guid.TryParseExact(id, "N", out var network) || network == Guid.Empty ||
            !PeerCredentials.IsSecret(secret))
            return null;
        try
        {
            return Tailnet.Address(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(address))) is { } target
                ? new(target, network, secret) : null;
        }
        catch (FormatException) { return null; }
    }

    private static PairingCode? ParseCompact(string value)
    {
        if (value.Length is not (40 or 43 or 50 or 52))
            return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value[Prefix.Length..]);
            if (bytes.Length is not (27 or 29 or 34 or 36))
                return null;
            var address = bytes.Length < 34 ? new byte[] { 100, 0, 0, 0 } : new byte[16];
            var prefix = address.Length == 4 ? 1 : Ipv6Prefix.Length;
            if (address.Length == 16)
                Ipv6Prefix.CopyTo(address);
            var host = address.Length - prefix;
            bytes.AsSpan(0, host).CopyTo(address.AsSpan(prefix));
            var ip = new IPAddress(address);
            var port = bytes.Length == host + 24 ? PeerLimits.DefaultPort :
                BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(host));
            if (!Tailnet.Contains(ip) || port == 0)
                return null;
            return new(new Uri(Tailnet.Origin(ip, port)), Guid.Empty, Convert.ToHexString(bytes.AsSpan(bytes.Length - 16)),
                Convert.ToHexString(bytes.AsSpan(bytes.Length - 24, 8)));
        }
        catch (FormatException) { return null; }
    }

    public static bool IsSecret(string? secret) => PeerCredentials.IsTemporarySecret(secret) || PeerCredentials.IsSecret(secret);
}
