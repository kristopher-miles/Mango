namespace Mango.Host;

internal sealed record AdminCommand(string Method, string Path, bool LocalOnly = false)
{
    internal static readonly IReadOnlyDictionary<string, AdminCommand> All = new Dictionary<string, AdminCommand>(StringComparer.Ordinal)
    {
        ["status"] = new("GET", "network"),
        ["scheduling"] = new("GET", "network/scheduling"),
        ["network refresh"] = new("PUT", "network/refresh"),
        ["network discover"] = new("PUT", "network/discover"),
        ["network queue"] = new("PUT", "network/queue"),
        ["network model"] = new("PUT", "network/models"),
        ["network provider"] = new("PUT", "network/providers"),
        ["network remove"] = new("DELETE", "network/nodes/{id}"),
        ["network create"] = new("PUT", "network/create", LocalOnly: true),
        ["network recover"] = new("PUT", "network/recover", LocalOnly: true),
        ["network password"] = new("PUT", "network/password"),
        ["network reset"] = new("PUT", "network/reset"),
        ["pair issue"] = new("POST", "network/pairing-code"),
        ["keys issue"] = new("POST", "network/keys"),
        ["keys revoke"] = new("DELETE", "network/keys/{id}"),
        ["settings show"] = new("GET", "settings"),
        ["settings patch"] = new("PATCH", "settings"),
        ["settings port"] = new("PUT", "node-port"),
        ["settings startup"] = new("PUT", "startup"),
        ["updates status"] = new("GET", "release"),
        ["updates check"] = new("POST", "release/check"),
        ["updates policy"] = new("PUT", "release/deferral"),
        ["updates share"] = new("POST", "release/share"),
        ["https status"] = new("GET", "public-ingress"),
        ["https set"] = new("PUT", "public-ingress"),
        ["https check"] = new("POST", "public-ingress/check"),
        ["providers list"] = new("GET", "providers"),
        ["providers set"] = new("PUT", "providers"),
        ["providers remove"] = new("DELETE", "providers/{id}"),
        ["providers stop"] = new("PUT", "providers/{id}/stop"),
        ["providers refresh"] = new("PUT", "providers/refresh"),
        ["providers serving"] = new("PUT", "providers/{id}/serving"),
        ["providers select"] = new("PUT", "providers/selection"),
        ["models set"] = new("PUT", "providers/models"),
        ["models serving"] = new("PUT", "providers/models/{id}/serving"),
        ["models yolo"] = new("PUT", "providers/models/{id}/yolo"),
        ["models ratings"] = new("PUT", "providers/ratings"),
        ["models load"] = new("PUT", "providers/lifecycle"),
        ["resources status"] = new("GET", "resources"),
        ["resources budget"] = new("PUT", "resources/budget"),
        ["resources benchmark"] = new("PUT", "resources/retry"),
        ["resources benchmark-missing"] = new("PUT", "resources/benchmark-missing"),
        ["resources release"] = new("PUT", "resources/release"),
        ["logs show"] = new("GET", "logs?day={day}"),
        ["logs export"] = new("GET", "logs/export?day={day}"),
        ["logs settings"] = new("PUT", "logs/settings"),
        ["logs collect"] = new("POST", "logs/collect"),
        ["logs clear"] = new("POST", "logs/clear")
    };

    internal string? Resolve(string? value)
    {
        if (Path.Contains("{day}", StringComparison.Ordinal))
            return value is null && Path.StartsWith("logs?", StringComparison.Ordinal) ? "logs" :
                DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _) ? Path.Replace("{day}", value, StringComparison.Ordinal) : null;
        if (!Path.Contains("{id}", StringComparison.Ordinal))
            return value is null ? Path : null;
        if (value is not { Length: > 0 and <= 160 } || value is "." or ".." ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            return null;
        return Path.Replace("{id}", Uri.EscapeDataString(value), StringComparison.Ordinal);
    }
}
