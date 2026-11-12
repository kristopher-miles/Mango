using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Mango.Host;

internal sealed record MangoRelease(string Version, int Window)
{
    public static MangoRelease Current { get; } = ReadBuild();

    [JsonIgnore] public string Wire { get; } = $"{Version}/{Window.ToString(CultureInfo.InvariantCulture)}";

    public static MangoRelease? Parse(string? wire)
    {
        if (wire is not { Length: > 2 and <= 48 })
            return null;
        var separator = wire.IndexOf('/');
        return separator > 0 && System.Version.TryParse(wire.AsSpan(0, separator), out _) &&
            int.TryParse(wire.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var window) && window > 0
            ? new(wire[..separator], window) : null;
    }

    private static MangoRelease ReadBuild()
    {
        var assembly = typeof(MangoRelease).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var window = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "MangoProtocolWindow").Value;
        return Parse($"{version}/{window}") ?? throw new InvalidOperationException("Mango's build release descriptor is invalid.");
    }
}
