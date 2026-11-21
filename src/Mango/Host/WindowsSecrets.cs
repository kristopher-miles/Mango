using System.Security.Cryptography;
using System.Text;

namespace Mango.Host;

internal static class WindowsSecrets
{
    public static string Protect(string secret, byte[]? context = null)
    {
#if WINDOWS
        var bytes = Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, context, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public static string Open(string protectedSecret, byte[]? context = null)
    {
#if WINDOWS
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), context, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
#else
        throw new PlatformNotSupportedException();
#endif
    }
}
