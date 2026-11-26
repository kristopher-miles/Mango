using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace Mango.Host;

internal static class PortBinding
{
    public static bool IsUnavailable(Exception error, bool addressMayChange = false)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } ||
                addressMayChange && cause is SocketException { SocketErrorCode: SocketError.AddressNotAvailable })
                return true;
        return false;
    }
}
