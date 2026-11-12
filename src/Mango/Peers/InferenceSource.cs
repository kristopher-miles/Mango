using System.Net;

namespace Mango.Peers;

internal enum SourceProvenance { Unknown, Direct, ProxyObserved, TrustedProxy }

internal sealed record InferenceSource(string? Address, SourceProvenance Provenance)
{
    public static readonly InferenceSource Unknown = new(null, SourceProvenance.Unknown);
    public bool Valid => Provenance == SourceProvenance.Unknown ? Address is null :
        Enum.IsDefined(Provenance) && Parse(Address) is not null;

    public static InferenceSource Of(HttpContext context) =>
        context.Items[typeof(InferenceSource)] as InferenceSource ??
        Observed(context.Connection.RemoteIpAddress, SourceProvenance.Direct);

    public static void Set(HttpContext context, InferenceSource source) => context.Items[typeof(InferenceSource)] = source;

    public static InferenceSource FromProxy(HttpContext context, string? configuredProxy)
    {
        var remote = context.Connection.RemoteIpAddress;
        var forwarded = context.Request.Headers["X-Forwarded-For"];
        if (remote is not null && remote.Equals(Parse(configuredProxy)) && forwarded.Count == 1 &&
            Parse(forwarded[0]) is { } client)
            return Observed(client, SourceProvenance.TrustedProxy);
        return Observed(remote, SourceProvenance.ProxyObserved);
    }

    public static IPAddress? Parse(string? text)
    {
        text = text?.Trim();
        if (text is not { Length: > 0 and <= 45 } || text.Contains('%') || text.Contains(',') ||
            !text.Contains(':') && text.Count(c => c == '.') != 3 || !IPAddress.TryParse(text, out var ip))
            return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static InferenceSource Observed(IPAddress? ip, SourceProvenance provenance) => ip is null
        ? Unknown : new((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(), provenance);
}
