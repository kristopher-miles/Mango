using System.Collections.Concurrent;
using System.Diagnostics;
using Mango.Host;

namespace Mango.Providers;

internal sealed record EndpointStatus(Guid Id, ProviderProfile Profile, ProviderMode Mode, string Address,
    Guid? CredentialReference, string? Executable, string? ModelFile, bool Online, string? Version,
    string? Error, DateTimeOffset? ObservedAt, bool CanLoad, bool CanUnload, bool CanStop, int? OwnedProcessId);

internal sealed record CatalogView(EndpointStatus[] Endpoints, Deployment[] Models, string? Selected, string? RecoveryWarning);
internal sealed record ProviderLifecycle(string DeploymentId, bool Loading, TimeSpan Elapsed);

internal sealed class ProviderCatalog(string directory) : IAsyncDisposable
{
    private sealed record Observation(ProviderInventory Inventory, string? Error, DateTimeOffset ObservedAt);

    private readonly SnapshotFile<ProviderSettings> store = new(Path.Combine(directory, "providers.json"), Validate);
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim operation = new(1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentDictionary<Guid, byte> pendingRefreshes = new();
    private readonly Dictionary<Guid, ProviderAdapter> adapters = [];
    private readonly Dictionary<Guid, Observation> observations = [];
    private readonly Dictionary<Guid, (CancellationTokenSource Stop, Task Work)> watchers = [];
    private readonly Dictionary<string, Guid> uncertainModels = [];
    private CatalogView view = new([], [], null, null);
    private string? activeModel;
    private ProviderExecution? lastExecution;
    private ProviderLifecycle? lastLifecycle;

    public CatalogView View
    {
        get
        {
            var snapshot = Volatile.Read(ref view);
            var active = Volatile.Read(ref activeModel);
            return active is null ? snapshot : snapshot with
            {
                Models = snapshot.Models.Select(model => model.Id == active ? model with { Busy = true } : model).ToArray()
            };
        }
    }
    public ProviderExecution? LastExecution => Volatile.Read(ref lastExecution);
    public ProviderLifecycle? LastLifecycle => Volatile.Read(ref lastLifecycle);

    public async Task InitializeAsync()
    {
        await store.LoadAsync(ProviderSettings.Empty);
        foreach (var endpoint in store.Value.Endpoints)
            adapters[endpoint.Id] = CreateAdapter(endpoint);
        Publish();
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await RefreshAsync(null, stopping);
            while (!stopping.IsCancellationRequested)
            {
                if (!await wake.WaitAsync(ProviderLimits.InventoryInterval, stopping))
                {
                    await RefreshAsync(null, stopping);
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stopping);
                foreach (var id in pendingRefreshes.Keys)
                {
                    if (!pendingRefreshes.TryRemove(id, out _))
                        continue;
                    await operation.WaitAsync(stopping);
                    try
                    {
                        if (adapters.ContainsKey(id))
                            await RefreshOneAsync(id, stopping);
                    }
                    finally { operation.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async Task<EndpointStatus> ConfigureAsync(EndpointUpdate input, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var current = store.Value;
            var previous = current.Endpoints.FirstOrDefault(endpoint => endpoint.Id == input.Id);
            if (input.Id.HasValue && previous is null)
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            if (previous is null && current.Endpoints.Length >= ProviderLimits.MaxEndpoints)
                throw new ProviderFailure(409, "The local provider registration limit was reached.");
            if (!Enum.IsDefined(input.Profile) || !Enum.IsDefined(input.Mode))
                throw new ProviderFailure(400, "Choose a supported provider profile and management mode.");
            if (input.Token is { Length: > ProviderLimits.MaxTokenLength } ||
                input.Token?.Any(character => character < '!' || character > '~') == true ||
                input.ClearToken && !string.IsNullOrEmpty(input.Token))
                throw new ProviderFailure(400, "Enter one valid provider bearer token or clear the saved token.");
            var address = NormalizeAddress(string.IsNullOrWhiteSpace(input.Address) ? DefaultAddress(input.Profile) : input.Address);
            var endpoint = new ProviderEndpoint(previous?.Id ?? Guid.NewGuid(), input.Profile, input.Mode, address,
                input.ClearToken ? null : !string.IsNullOrEmpty(input.Token) ? ProviderSecrets.Protect(input.Token)
                    : previous?.Address == address && previous.Profile == input.Profile ? previous.ProtectedToken : null,
                BlankToNull(input.Executable), BlankToNull(input.ModelFile));
            ValidateEndpoint(endpoint);
            if (previous is not null && previous != endpoint && adapters[endpoint.Id].OwnedProcessId.HasValue)
                throw new ProviderFailure(409, "Stop the process Mango started before changing this registration.");
            if (previous != endpoint && uncertainModels.ContainsValue(endpoint.Id))
                throw new ProviderFailure(409, "Confirm the provider has stopped the prior request before changing its registration.");
            if (current.Endpoints.Any(existing => existing.Id != endpoint.Id && existing.Address == endpoint.Address))
                throw new ProviderFailure(409, "This provider address is already registered.");
            await store.UpdateAsync(value => value with
            {
                Endpoints = value.Endpoints.Where(item => item.Id != endpoint.Id).Append(endpoint).ToArray()
            }, cancellation);
            if (previous != endpoint)
            {
                await StopWatchAsync(endpoint.Id);
                adapters[endpoint.Id] = CreateAdapter(endpoint);
                observations.Remove(endpoint.Id);
                Publish();
            }
            await RefreshOneAsync(endpoint.Id, cancellation);
            return View.Endpoints.Single(item => item.Id == endpoint.Id);
        }
        finally { operation.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            if (!adapters.TryGetValue(id, out var adapter))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            if (adapter.OwnedProcessId.HasValue)
                throw new ProviderFailure(409, "Stop the process Mango started before removing this registration.");
            if (uncertainModels.ContainsValue(id))
                throw new ProviderFailure(409, "Confirm the provider has stopped the prior request before removing it.");
            var modelIds = View.Models.Where(model => model.EndpointId == id).Select(model => model.Id).ToHashSet();
            await store.UpdateAsync(value => value with
            {
                Endpoints = value.Endpoints.Where(item => item.Id != id).ToArray(),
                Models = value.Models.Where(pair => !modelIds.Contains(pair.Key)).ToDictionary(),
                Selected = value.Selected is { } selected && modelIds.Contains(selected) ? null : value.Selected
            }, cancellation);
            adapters.Remove(id);
            observations.Remove(id);
            await StopWatchAsync(id);
            Publish();
        }
        finally { operation.Release(); }
    }

    public async Task RefreshAsync(Guid? id, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            if (id.HasValue && !adapters.ContainsKey(id.Value))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            foreach (var endpoint in store.Value.Endpoints)
                if (!id.HasValue || id == endpoint.Id)
                    await RefreshOneAsync(endpoint.Id, cancellation);
        }
        finally { operation.Release(); }
    }

    public async Task EditAsync(ModelUpdate input, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var model = Find(input.Id);
            if (model.Options.Revision != input.Revision)
                throw new ProviderFailure(409, "This model changed. Refresh the catalog before editing.");
            var updated = new ModelOptions(input.Alias?.Trim() ?? "", input.Ratings ?? [],
                input.ServedContext, input.EmbeddingDimensions, BlankToNull(input.EmbeddingFamily),
                input.MediaTokensPerItem, model.Options.Revision + 1);
            ValidateModel(updated);
            RequireCompatibleAlias(model with { Options = updated }, View.Models);
            await store.UpdateAsync(value => value with { Models = new(value.Models) { [model.Id] = updated } }, cancellation);
            Publish();
        }
        finally { operation.Release(); }
    }

    public async Task ApplyRatingsAsync(string alias, Dictionary<string, int?> ratings, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var matches = View.Models.Where(item => SameAlias(item.Alias, alias)).ToArray();
            if (matches.Length == 0)
                throw new ProviderFailure(404, "No deployments have this alias.");
            var updated = new Dictionary<string, ModelOptions>(store.Value.Models);
            foreach (var model in matches)
            {
                var values = new Dictionary<string, int?>(model.Options.Ratings, StringComparer.Ordinal);
                foreach (var rating in ratings)
                    values[rating.Key] = rating.Value;
                var next = model.Options with { Ratings = values, Revision = model.Options.Revision + 1 };
                ValidateModel(next);
                updated[model.Id] = next;
            }
            await store.UpdateAsync(value => value with { Models = updated }, cancellation);
            Publish();
        }
        finally { operation.Release(); }
    }

    public async Task SelectAsync(string? id, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            if (id is not null)
            {
                var model = Find(id);
                RequireCompatibleAlias(model, View.Models);
                if (!model.Eligible)
                    throw new ProviderFailure(409, "Choose a loaded model with a verified or configured limit.");
            }
            await store.UpdateAsync(value => value with { Selected = id }, cancellation);
            Publish();
        }
        finally { operation.Release(); }
    }

    public async Task ManageAsync(string id, bool load, CancellationToken cancellation)
    {
        if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(409, "A provider operation is in progress.");
        try
        {
            var model = Find(id);
            var adapter = adapters[model.EndpointId];
            if (load && !model.Model.SupportsLoad || !load && !model.Model.SupportsUnload)
                throw new ProviderFailure(409, "The provider does not expose this lifecycle operation for the deployment.");
            if (load && uncertainModels.Count > 0)
                throw new ProviderFailure(409, "Mango has not confirmed that the prior request stopped.");
            if (load && !model.Model.Installed)
                throw new ProviderFailure(409, "This model is not confirmed installed.");
            if (load && model.Instance is not null)
                throw new ProviderFailure(409, "This deployment is already loaded.");
            if (!load && model.Instance is null && !adapter.OwnedProcessId.HasValue)
                throw new ProviderFailure(409, "The model is already unloaded or its state is unknown.");
            var started = Stopwatch.GetTimestamp();
            if (load)
                await adapter.LoadAsync(model.Model, model.Options.ServedContext, cancellation);
            else
                await adapter.UnloadAsync(model.Model, cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                do
                {
                    await RefreshOneAsync(model.EndpointId, timeout.Token);
                    var endpoint = View.Endpoints.First(status => status.Id == model.EndpointId);
                    var observed = View.Models.FirstOrDefault(item => item.Id == id);
                    if (load ? endpoint.Online && observed?.Instance is not null
                        : endpoint.Online && observed?.Instance is null ||
                          !endpoint.Online && !adapter.OwnedProcessId.HasValue && endpoint.Profile is ProviderProfile.LlamaSingle or ProviderProfile.LlamaEmbedding)
                    {
                        Volatile.Write(ref lastLifecycle, new(id, load, Stopwatch.GetElapsedTime(started)));
                        return;
                    }
                    await Task.Delay(500, timeout.Token);
                } while (true);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new ProviderFailure(502, "Mango could not confirm the requested model state.");
            }
        }
        catch (ProviderFailure error) when (error.Status is >= 500 or 409)
        {
            if (View.Models.FirstOrDefault(model => model.Id == id) is { } target)
                SignalRefresh(target.EndpointId);
            throw;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (View.Models.FirstOrDefault(model => model.Id == id) is { } target)
                SignalRefresh(target.EndpointId);
            throw;
        }
        finally { operation.Release(); }
    }

    public async Task StopAsync(Guid id, CancellationToken cancellation)
    {
        if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(409, "A provider operation is in progress.");
        try
        {
            if (!adapters.TryGetValue(id, out var adapter))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            await adapter.StopAsync(cancellation);
            await RefreshOneAsync(id, cancellation);
        }
        finally { operation.Release(); }
    }

    public async Task ExecuteAsync(InferenceOperation operationKind, HttpContext context, CancellationToken cancellation)
    {
        if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(429, "A local provider operation is in progress.");
        Deployment? model = null;
        InferenceRequest? payload = null;
        try
        {
            if (uncertainModels.Count > 0)
                throw new ProviderFailure(409, "Mango has not confirmed that the prior request stopped. Refresh or unload the previous deployment.");
            payload = await InferenceRequest.ReadAsync(operationKind, context.Request, cancellation);
            if (SameAlias(payload.Alias, "best"))
                throw new ProviderFailure(501, "Automatic best-model selection is not available.");
            var snapshot = View;
            model = snapshot.Models.FirstOrDefault(item => item.Id == snapshot.Selected && SameAlias(item.Alias, payload.Alias));
            if (model is null || !model.Available)
                throw new ProviderFailure(409, "Select this loaded deployment in the local Mango catalog before sending requests.");
            RequireCompatibleAlias(model, snapshot.Models);
            Volatile.Write(ref activeModel, model.Id);
            var execution = await payload.ForwardAsync(model, adapters[model.EndpointId], context, cancellation);
            Volatile.Write(ref lastExecution, execution);
        }
        catch (ProviderFailure error) when (model is not null && error.Status is >= 500 or 409)
        {
            SignalRefresh(model.EndpointId);
            throw;
        }
        catch (Exception error) when (model is not null && error is IOException or OperationCanceledException)
        {
            SignalRefresh(model.EndpointId);
            throw;
        }
        finally
        {
            if (model is not null && payload is { Submitted: true, Settled: false })
            {
                if (!await ObserveIdleAsync(adapters[model.EndpointId], model.Model, wait: true, CancellationToken.None))
                {
                    uncertainModels[model.Id] = model.EndpointId;
                    Publish();
                }
            }
            payload?.Dispose();
            Volatile.Write(ref activeModel, null);
            operation.Release();
        }
    }

    private void SignalRefresh(Guid id)
    {
        pendingRefreshes.TryAdd(id, 0);
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RefreshOneAsync(Guid id, CancellationToken cancellation)
    {
        try
        {
            var adapter = adapters[id];
            var result = await adapter.InspectAsync(cancellation);
            observations[id] = new(result, null, DateTimeOffset.UtcNow);
            if (adapter is LlamaAdapter { HasInventoryEvents: true } llama && !watchers.ContainsKey(id))
            {
                var stop = new CancellationTokenSource();
                watchers[id] = (stop, llama.WatchInventoryAsync(() => SignalRefresh(id), stop.Token));
            }
            foreach (var key in uncertainModels.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
            {
                var model = result.Models.FirstOrDefault(model => model.Id == key);
                if (model is null || model.Instances.Length == 0 ||
                    await ObserveIdleAsync(adapter, model, wait: false, cancellation))
                    uncertainModels.Remove(key);
            }
        }
        catch (ProviderFailure error)
        {
            var previous = observations.GetValueOrDefault(id)?.Inventory;
            var adapter = adapters[id];
            var fallback = adapter is LlamaAdapter { ConfiguredModel: { } configured }
                ? new ProviderInventory(previous?.Version, [configured], true, adapter.OwnedProcessId.HasValue,
                    adapter.OwnedProcessId.HasValue, adapter.OwnedProcessId)
                : previous ?? new ProviderInventory(null, [], false, false, false);
            observations[id] = new(fallback, error.Message, DateTimeOffset.UtcNow);
            if (fallback.CanLoad && !adapter.OwnedProcessId.HasValue && previous?.CanStop == true)
                foreach (var key in uncertainModels.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
                    uncertainModels.Remove(key);
        }
        Publish();
    }

    private void Publish()
    {
        var value = store.Value;
        var endpoints = new List<EndpointStatus>();
        var models = new List<Deployment>();
        foreach (var endpoint in value.Endpoints)
        {
            var observation = observations.GetValueOrDefault(endpoint.Id);
            var inventory = observation?.Inventory;
            var online = observation is { Error: null };
            endpoints.Add(new(endpoint.Id, endpoint.Profile, endpoint.Mode, endpoint.Address,
                endpoint.ProtectedToken is not null ? endpoint.Id : null, endpoint.Executable, endpoint.ModelFile,
                online, inventory?.Version, observation?.Error, observation?.ObservedAt,
                inventory?.CanLoad == true && (online || endpoint.Executable is not null),
                inventory?.CanUnload == true && (online || inventory.CanStop),
                inventory?.CanStop == true, inventory?.OwnedProcessId));
            foreach (var model in inventory?.Models ?? [])
            {
                var options = value.Models.GetValueOrDefault(model.Id) ?? new ModelOptions(DefaultAlias(model), [], null, null, null, null, 0);
                models.Add(new(endpoint.Id, model, options, online, uncertainModels.ContainsKey(model.Id), inventory?.Version));
            }
        }
        Volatile.Write(ref view, new(endpoints.ToArray(), models.ToArray(), value.Selected, store.RecoveryWarning));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in watchers.Keys.ToArray())
            await StopWatchAsync(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var adapter in adapters.Values.Where(adapter => adapter.OwnedProcessId.HasValue))
        {
            try { await adapter.StopAsync(timeout.Token); }
            catch (Exception error) when (error is OperationCanceledException or System.ComponentModel.Win32Exception or ProviderFailure)
            {
                Console.Error.WriteLine("Mango could not confirm shutdown of an owned provider process.");
            }
        }
        http.Dispose();
        operation.Dispose();
        wake.Dispose();
    }

    private async Task StopWatchAsync(Guid id)
    {
        if (!watchers.Remove(id, out var watcher))
            return;
        using var stop = watcher.Stop;
        await stop.CancelAsync();
        try { await watcher.Work; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static async Task<bool> ObserveIdleAsync(ProviderAdapter adapter, ProviderModel model, bool wait,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            do
            {
                var idle = await adapter.CheckIdleAsync(model, timeout.Token);
                if (idle != false || !wait)
                    return idle == true;
                await Task.Delay(200, timeout.Token);
            } while (true);
        }
        catch (Exception error) when (error is ProviderFailure or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private ProviderAdapter CreateAdapter(ProviderEndpoint endpoint) => endpoint.Profile switch
    {
        ProviderProfile.LmStudio => new LmStudioAdapter(http, endpoint),
        ProviderProfile.UnslothStudio or ProviderProfile.UnslothRun => new UnslothAdapter(http, endpoint),
        _ => new LlamaAdapter(http, endpoint)
    };

    private Deployment Find(string id) => View.Models.FirstOrDefault(item => item.Id == id)
        ?? throw new ProviderFailure(404, "The deployment was not found in the local inventory.");

    private static string DefaultAlias(ProviderModel model)
    {
        var basename = model.NativeId.Contains('\\') || Path.IsPathRooted(model.NativeId)
            ? Path.GetFileNameWithoutExtension(model.NativeId) : model.NativeId;
        return ValidAlias(basename) ? basename : $"model-{model.Id[..12].ToLowerInvariant()}";
    }

    internal static string DefaultAddress(ProviderProfile profile) => profile switch
    {
        ProviderProfile.LmStudio => "http://127.0.0.1:1234",
        ProviderProfile.UnslothStudio or ProviderProfile.UnslothRun => "http://127.0.0.1:8888",
        _ => "http://127.0.0.1:8080"
    };

    private static string NormalizeAddress(string address)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Port is <= 0 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new ProviderFailure(400, "Use a loopback HTTP or HTTPS provider origin without credentials, paths, or query parameters.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static void ValidateEndpoint(ProviderEndpoint endpoint)
    {
        _ = NormalizeAddress(endpoint.Address);
        if (endpoint.Id == Guid.Empty || !Enum.IsDefined(endpoint.Profile) || !Enum.IsDefined(endpoint.Mode))
            throw new ProviderFailure(400, "The provider registration is invalid.");
        if (endpoint.Executable is null && endpoint.ModelFile is null)
            return;
        var uri = new Uri(endpoint.Address);
        if (endpoint.Mode != ProviderMode.Managed || endpoint.Profile is not (ProviderProfile.LlamaSingle or ProviderProfile.LlamaEmbedding) ||
            uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
            !LocalPath(endpoint.Executable) || !LocalPath(endpoint.ModelFile) ||
            !string.Equals(Path.GetFileName(endpoint.Executable), "llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(endpoint.ModelFile), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure(400, "Launching llama-server requires managed single-model mode, IPv4 loopback HTTP, and absolute local executable and GGUF paths.");
    }

    private static bool LocalPath(string? path) => path is { Length: >= 3 and <= 1024 } && char.IsAsciiLetter(path[0]) &&
        path[1] == ':' && path[2] is '\\' or '/' && Path.IsPathFullyQualified(path);

    private static string? Validate(ProviderSettings value)
    {
        try
        {
            if (value.Endpoints is null || value.Models is null || value.Endpoints.Length > ProviderLimits.MaxEndpoints ||
                value.Endpoints.Any(endpoint => endpoint is null) ||
                value.Endpoints.DistinctBy(endpoint => endpoint.Id).Count() != value.Endpoints.Length)
                return "The local provider configuration is invalid.";
            foreach (var endpoint in value.Endpoints)
                ValidateEndpoint(endpoint);
            foreach (var model in value.Models.Values)
                ValidateModel(model);
            return null;
        }
        catch (ProviderFailure error) { return error.Message; }
    }

    private static void ValidateModel(ModelOptions value)
    {
        if (value is null || !ValidAlias(value.Alias) || value.Ratings is null || value.Ratings.Count > 64 ||
            value.Ratings.Any(rating => string.IsNullOrWhiteSpace(rating.Key) || rating.Key.Length > 100 ||
                rating.Key.Any(char.IsControl) || rating.Value is < 0 or > 5) ||
            value.ServedContext is < 1 or > ProviderLimits.MaxContext ||
            value.EmbeddingDimensions is < 1 or > ProviderLimits.MaxEmbeddingDimensions ||
            value.MediaTokensPerItem is < 1 or > ProviderLimits.MaxContext ||
            value.EmbeddingFamily is { } family && !ValidAlias(family) || value.Revision < 0)
            throw new ProviderFailure(400, "Use a valid model alias, ratings from 0 to 5, and positive model limits.");
    }

    internal static void RequireCompatibleAlias(Deployment model, IEnumerable<Deployment> others)
    {
        foreach (var other in others.Where(item => item.Id != model.Id && SameAlias(item.Alias, model.Alias)))
        {
            if (!model.Model.Inputs.ToHashSet().SetEquals(other.Model.Inputs) ||
                !model.Model.Outputs.ToHashSet().SetEquals(other.Model.Outputs))
                throw new ProviderFailure(409, "Deployments sharing an alias must have identical input and output modalities.");
            if (model.Model.Operations.Contains(InferenceOperation.Embeddings) &&
                (model.EmbeddingDimensions is { } dimensions && other.EmbeddingDimensions is { } otherDimensions && dimensions != otherDimensions ||
                 !model.Model.EmbeddingEncodings.Intersect(other.Model.EmbeddingEncodings).Any()))
                throw new ProviderFailure(409, "Embeddings under one alias must have compatible dimensions and encodings.");
        }
    }

    private static bool ValidAlias(string? value) => value is { Length: > 0 and <= 200 } && !SameAlias(value, "best") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '/' or ':');
    private static bool SameAlias(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
