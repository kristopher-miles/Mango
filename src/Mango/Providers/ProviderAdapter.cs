using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Host;

namespace Mango.Providers;

internal abstract class ProviderAdapter(HttpClient http, ProviderEndpoint endpoint)
{
    protected ProviderEndpoint Endpoint { get; } = endpoint;
    protected bool Managed => Endpoint.Mode == ProviderMode.Managed;
    public virtual int? OwnedProcessId => null;
    public abstract Task<ProviderInventory> InspectAsync(CancellationToken cancellation);

    public virtual Task LoadAsync(ProviderModel model, int? context, CancellationToken cancellation) =>
        Task.FromException(new ProviderFailure(409, "This endpoint has no authorized load operation."));
    public virtual Task UnloadAsync(ProviderModel model, CancellationToken cancellation) =>
        Task.FromException(new ProviderFailure(409, "This endpoint has no authorized unload operation."));
    public virtual Task StopAsync(CancellationToken cancellation) =>
        Task.FromException(new ProviderFailure(409, "Mango does not own this provider process."));

    public virtual Task<int?> CountChatAsync(JsonObject payload, CancellationToken cancellation) => Task.FromResult<int?>(null);
    public virtual Task<int?> CountEmbeddingAsync(JsonNode input, string nativeId, CancellationToken cancellation) =>
        Task.FromResult<int?>(null);
    public virtual Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation) => Task.FromResult<bool?>(null);
    public virtual Task ConfirmDeploymentAsync(InferenceOperation operation, ProviderModel model, ProviderInstance instance,
        CancellationToken cancellation) => Task.CompletedTask;
    public virtual void Prepare(InferenceOperation operation, JsonObject payload) { }
    protected virtual string InferenceQuery => "";

    public Task<HttpResponseMessage> ExecuteAsync(InferenceOperation operation, HttpContent content, CancellationToken cancellation) =>
        SendAsync(HttpMethod.Post, InferencePaths.Path(operation) + InferenceQuery, content, cancellation);

    protected async Task<JsonElement> JsonAsync(string path, CancellationToken cancellation,
        object? body = null, bool optional = false, bool lifecycle = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(lifecycle ? ProviderLimits.LifecycleTimeout : ProviderLimits.ControlTimeout);
        try
        {
            using var response = await SendAsync(body is null ? HttpMethod.Get : HttpMethod.Post, path,
                body is null ? null : JsonContent.Create(body), timeout.Token);
            if (optional && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or
                HttpStatusCode.NotImplemented or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return default;
            RequireSuccess(response);
            var bytes = await ReadBoundedAsync(response.Content, ProviderLimits.MaxControlBytes, timeout.Token);
            if (bytes.Length == 0 && lifecycle)
                return default;
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderFailure(504, "The provider did not respond in time.");
        }
        catch (JsonException)
        {
            throw new ProviderFailure(502, "The provider returned invalid inventory or control data.");
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > limit)
            throw new ProviderFailure(502, "The provider response exceeded the supported size.");
        await using var stream = await content.ReadAsStreamAsync(cancellation);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            if (result.Length + count > limit)
                throw new ProviderFailure(502, "The provider response exceeded the supported size.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Endpoint.Address), path)) { Content = content };
        if (Endpoint.ProtectedToken is { } protectedToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ProviderSecrets.Open(protectedToken));
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        }
        catch (HttpRequestException)
        {
            throw new ProviderFailure(502, "Mango could not reach the configured provider.");
        }
    }

    public static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var status = (int)response.StatusCode;
        throw new ProviderFailure(status switch
        {
            400 or 409 or 413 or 422 or 429 or 503 or 504 => status,
            _ => 502
        }, status switch
        {
            401 or 403 => "The provider refused its configured credential or management permission.",
            404 or 405 or 501 => "The provider does not expose this operation for the selected deployment.",
            400 or 422 => "The provider rejected the request parameters or input limits.",
            409 => "The provider is busy or its model state has changed.",
            429 or 503 => "The provider is temporarily unavailable.",
            _ => $"The provider returned HTTP {status}."
        });
    }

    protected ProviderModel Model(string nativeId, string displayName, string? variant, string[] inputs, string[] outputs,
        InferenceOperation[] operations, ProviderInstance[] instances, int? theoreticalContext, bool tools = false,
        bool structured = false, int? dimensions = null, string[]? encodings = null, long? artifactBytes = null,
        string? managementId = null, bool installed = true, bool supportsLoad = true, bool supportsUnload = true, string? format = null) =>
        new(ProviderIdentity.Model(Endpoint.Id, nativeId), nativeId, managementId ?? nativeId, displayName,
            variant, inputs, outputs, operations, instances, theoreticalContext, tools, structured,
            dimensions, encodings ?? [], artifactBytes, installed, supportsLoad, supportsUnload, format);

}

internal static class ProviderJson
{
    public static JsonElement Get(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    public static string? Text(this JsonElement value, string name) =>
        value.Get(name) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;
    public static int? Number(this JsonElement value, string name) =>
        value.Get(name) is { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var number) && number > 0
            ? number : null;
    public static long? Long(this JsonElement value, string name) =>
        value.Get(name) is { ValueKind: JsonValueKind.Number } element && element.TryGetInt64(out var number) && number > 0
            ? number : null;
    public static bool IsTrue(this JsonElement value, string name) => value.Get(name).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    public static string[] Strings(this JsonElement value) => value.Items()
        .Where(item => item.ValueKind == JsonValueKind.String)
        .Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

internal static class ProviderSecrets
{
    public static string Protect(string secret)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(secret);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Open(string protectedToken)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null, DataProtectionScope.CurrentUser);
            try { return System.Text.Encoding.UTF8.GetString(bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new ProviderFailure(502, "The provider credential cannot be unlocked by this Windows user. Enter it again locally.");
        }
    }
}

internal static class InferencePaths
{
    public static string Path(InferenceOperation operation) => operation switch
    {
        InferenceOperation.Chat => "/v1/chat/completions",
        InferenceOperation.Embeddings => "/v1/embeddings",
        InferenceOperation.Images => "/v1/images/generations",
        InferenceOperation.Transcription => "/v1/audio/transcriptions",
        InferenceOperation.Speech => "/v1/audio/speech",
        InferenceOperation.SystemOne => "/v1/systemone",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}
