using Mango.Host;
using Mango.Providers;

namespace Mango.Resources;

internal sealed record BudgetUpdate(string Deployment, InferenceOperation Operation, long Units, int Items, int MediaItems,
    long? LoadRamMiB, long? ExecuteRamMiB, Dictionary<string, long>? LoadVramMiB, Dictionary<string, long>? ExecuteVramMiB,
    bool AllowOverlap, int ParallelSlots, bool AllowAttached);

internal sealed partial class ResourceProtection
{
    private bool benchmarksReady;
    private string? benchmarkDeployment;
    private string? benchmarkPass;
    private (string Deployment, InferenceOperation Operation)? requestedBenchmark;
    private MeasurementWindow? measurement;

    public bool ServingAllowed
    {
        get
        {
            lock (sync)
                return status.State == ContributionState.Contributor && settings.Value.Role == LocalRole.Contributor &&
                    (settings.Value.BenchmarkPolicy != BenchmarkPolicy.Now || benchmarksReady);
        }
    }

    public void RequireServing()
    {
        lock (sync)
            if (!ServingAllowed)
                throw new ProviderFailure(503, status.Reason ??
                    "The local benchmark policy requires complete profiles before inference.");
    }

    public ResourceBudget? Estimate(Deployment model, EndpointStatus endpoint, InferenceOperation operation)
    {
        lock (sync)
            return FindBudget(model, endpoint, operation);
    }

    private sealed record Target(Deployment Model, EndpointStatus Endpoint, InferenceOperation Operation, string Configuration,
        string Runtime, ResourceBudget? Budget, BenchmarkProfile? Profile)
    {
        public bool CanColdLoad => Endpoint.CanLoad && (Endpoint.CanUnload || Endpoint.Executable is not null) &&
            Model.Model.SupportsLoad && Model.Model.SupportsUnload;
    }
    private sealed class MeasurementWindow(ResourceObservation? before, Guid endpoint)
    {
        public readonly ResourceObservation? Before = before;
        public readonly Guid Endpoint = endpoint;
        public long Ram;
        public readonly Dictionary<string, long> Vram = [];
        public long? WorkerPeak;
        public int Samples;
    }

    public object View(ProviderCatalog providers)
    {
        var catalog = providers.View;
        lock (sync)
        {
            var targets = Targets(catalog).Select(target => new
            {
                Id = target.Model.Id,
                target.Model.Alias,
                target.Operation,
                Budget = target.Budget,
                Profile = target.Profile,
                Unsafe = target.Budget is { } budget && unsafePlacements.Contains(FaultKey(budget)),
                target.Model.Context,
                Attached = target.Endpoint.Mode == ProviderMode.Attached,
                target.CanColdLoad
            }).ToArray();
            return new
            {
                Status = status, Observation = observed, LastEvent = lastEvent, BenchmarkReady = benchmarksReady,
                Benchmark = benchmarkDeployment is not null ? new { Deployment = benchmarkDeployment, Pass = benchmarkPass }
                    : requestedBenchmark is { } requested ? new { requested.Deployment, Pass = (string?)"Waiting for local resources" } : null,
                Models = targets, RecoveryWarning = store.RecoveryWarning
            };
        }
    }

    public async Task SaveBudgetAsync(BudgetUpdate update, ProviderCatalog providers, CancellationToken cancellation)
    {
        ResourceBudget budget;
        lock (sync)
        {
            var target = Targets(providers.View).FirstOrDefault(item => item.Model.Id == update.Deployment &&
                item.Operation == update.Operation) ?? throw new ProviderFailure(404, "This local deployment and operation are unavailable.");
            if (reservations.Any(item => item.Deployment == update.Deployment && !item.Settled))
                throw new ProviderFailure(409, "Wait for this deployment's work to settle before changing its memory limits.");
            if (observed?.Hardware is null)
                throw new ProviderFailure(503, "Wait for Windows to report the local devices.");
            budget = new(target.Model.Id, target.Configuration, target.Model.Instance?.ResourceFingerprint,
                new(update.Operation, update.Units, update.Items, update.MediaItems),
                FromMiB(update.LoadRamMiB, update.LoadVramMiB), FromMiB(update.ExecuteRamMiB, update.ExecuteVramMiB),
                update.AllowOverlap, update.ParallelSlots, update.AllowAttached);
            if (!ValidBudget(budget) || !budget.AllowOverlap && budget.ParallelSlots != 1)
                throw new ProviderFailure(400, "Enter valid memory peaks, workload limits, and qualified slot counts.");
        }
        await store.UpdateAsync(data =>
        {
            bool Matches(ResourceBudget item) => item.Deployment == budget.Deployment &&
                item.Workload.Operation == budget.Workload.Operation;
            bool MatchesProfile(BenchmarkProfile item) => item.Deployment == budget.Deployment &&
                item.Operation == budget.Workload.Operation;
            var previous = data.Budgets.FirstOrDefault(Matches);
            var changedWorkload = previous is not null && previous.Workload != budget.Workload;
            return data with
            {
                Budgets = data.Budgets.Where(item => !Matches(item)).Append(budget).ToArray(),
                Unsafe = data.Unsafe.Where(item => item != FaultKey(budget)).ToArray(),
                Profiles = changedWorkload ? data.Profiles.Where(item => !MatchesProfile(item)).ToArray()
                    : data.Profiles.Select(item => MatchesProfile(item) ? item with { Error = null } : item).ToArray()
            };
        }, cancellation);
        lock (sync)
            unsafePlacements.Remove(FaultKey(budget));
        CatalogChanged(providers.View);
        Changed?.Invoke();
    }

    private MemoryCost FromMiB(long? host, Dictionary<string, long>? vram)
    {
        vram ??= [];
        if (host is not (>= 0 and <= LocalSettings.MaximumReserveMiB) ||
            observed!.Devices.Any(device => !device.Integrated && !vram.ContainsKey(device.Id)) ||
            vram.Any(item => item.Value is < 0 or > LocalSettings.MaximumReserveMiB ||
                !observed!.Devices.Any(device => device.Id == item.Key && !device.Integrated && device.TotalBytes is not null)))
            throw new ProviderFailure(400, "Enter RAM and per-device VRAM limits in MiB. Enter zero for a discrete GPU the provider does not use.");
        return new(host.Value * MiB, vram.ToDictionary(item => item.Key, item => item.Value * MiB));
    }

    public async Task RetryBenchmarkAsync(string deployment, InferenceOperation operation, ProviderCatalog providers,
        CancellationToken cancellation)
    {
        var target = Targets(providers.View).FirstOrDefault(item => item.Model.Id == deployment && item.Operation == operation)
            ?? throw new ProviderFailure(404, "This deployment was not found.");
        if (target.Budget is not { } budget)
            throw new ProviderFailure(409, "Set a local memory limit before benchmarking this deployment.");
        lock (sync)
            if (benchmarkDeployment == deployment && benchmark is { IsCompleted: false } ||
                reservations.Any(item => item.Deployment == deployment && !item.Settled))
                throw new ProviderFailure(409, "Wait for this deployment's benchmark to settle before retrying.");
        await store.UpdateAsync(data => data with
        {
            Unsafe = data.Unsafe.Where(item => item != FaultKey(budget)).ToArray(),
            Profiles = data.Profiles.Where(item => item.Deployment != deployment || item.Operation != operation).ToArray()
        }, cancellation);
        lock (sync)
        {
            unsafePlacements.Remove(FaultKey(budget));
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                SetBenchmarkReady(false);
            requestedBenchmark = (deployment, operation);
        }
        Changed?.Invoke();
        Signal();
    }

    internal InferenceOperation LoadOperation(Deployment model, EndpointStatus endpoint)
    {
        lock (sync)
            return Operations(model, endpoint).FirstOrDefault(operation => FindBudget(model, endpoint, operation) is not null,
                model.Model.Operations.FirstOrDefault());
    }

    private static string FaultKey(ResourceBudget budget) =>
        ProviderIdentity.Hash(budget.Deployment, budget.Configuration, budget.Workload.Operation.ToString());

    private string Configuration(Deployment model) => ProviderIdentity.Hash(model.Configuration, model.Id,
        observed?.Hardware, observed?.TotalRam.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private ResourceBudget? FindBudget(Deployment model, EndpointStatus endpoint, InferenceOperation operation)
    {
        var configuration = Configuration(model);
        var runtime = model.Instance?.ResourceFingerprint;
        var stored = store.Value.Budgets.FirstOrDefault(item => item.Deployment == model.Id &&
            item.Workload.Operation == operation && item.Configuration == configuration &&
            (item.Runtime is null || runtime is null || item.Runtime == runtime));
        if (stored is null)
            return null;
        var profile = MatchProfile(model.Id, operation, configuration, runtime);
        if (profile is null)
            return stored;
        var run = stored.Execute;
        var load = stored.Load;
        foreach (var pass in profile.Measurements)
            if (pass.ObservedIncrement is { } observedCost)
            {
                if (pass.Pass == "cold")
                    load = Maximum(load, observedCost);
                else
                    run = Maximum(run, observedCost);
            }
        return stored with { Load = load, Execute = run };
    }

    private static MemoryCost Maximum(MemoryCost first, MemoryCost second) => new(Math.Max(first.RamBytes, second.RamBytes),
        first.DeviceBytes.Keys.Concat(second.DeviceBytes.Keys).Distinct().ToDictionary(id => id,
            id => Math.Max(first.DeviceBytes.GetValueOrDefault(id), second.DeviceBytes.GetValueOrDefault(id))));

    private BenchmarkProfile? MatchProfile(string id, InferenceOperation operation, string configuration, string? runtime) =>
        store.Value.Profiles.FirstOrDefault(item => item.Deployment == id && item.Operation == operation &&
            item.Configuration == configuration && (runtime is null || item.Runtime == runtime));

    private InferenceOperation[] Operations(Deployment model, EndpointStatus endpoint) => model.Model.Operations.Length != 0
        ? model.Model.Operations
        : endpoint.Executable is null ? [] : endpoint.Profile == ProviderProfile.LlamaEmbedding
            ? [InferenceOperation.Embeddings]
            : store.Value.Budgets.Where(item => item.Deployment == model.Id).Select(item => item.Workload.Operation)
                .Distinct().DefaultIfEmpty(InferenceOperation.Chat).ToArray();

    private Target[] Targets(CatalogView catalog)
    {
        lock (sync)
        {
            var endpoints = catalog.Endpoints.ToDictionary(item => item.Id);
            return catalog.Models.Where(item => item.Model.Installed && endpoints.ContainsKey(item.EndpointId))
                .SelectMany(model =>
                {
                    var endpoint = endpoints[model.EndpointId];
                    var config = Configuration(model);
                    var runtime = model.Instance?.ResourceFingerprint;
                    var operations = Operations(model, endpoint);
                    return operations.Select(operation => new Target(model, endpoint, operation, config, runtime ?? "",
                        FindBudget(model, endpoint, operation), MatchProfile(model.Id, operation, config, runtime)));
                }).ToArray();
        }
    }

    internal void AdvanceBenchmarks(ProviderCatalog providers, CancellationToken stopping)
    {
        lock (sync)
        {
            var current = settings.Value;
            if (benchmark is { IsCompleted: true })
            {
                benchmarkStop?.Dispose();
                benchmarkStop = null;
                benchmark = null;
                benchmarkDeployment = benchmarkPass = null;
            }
            var catalog = providers.View;
            var pending = Targets(catalog).Where(IsPending).ToArray();
            SetBenchmarkReady(current.BenchmarkPolicy == BenchmarkPolicy.Never || pending.Length == 0);
            if (benchmark is not null || status.State != ContributionState.Contributor ||
                reservations.Any(item => !item.Settled) || observed is null)
                return;
            var now = clock.GetUtcNow();
            lastClient ??= now;
            if (requestedBenchmark is null && (current.BenchmarkPolicy == BenchmarkPolicy.Never ||
                current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient && now - lastClient < BenchmarkIdle))
                return;
            var target = pending.FirstOrDefault(item =>
                (requestedBenchmark is null || requestedBenchmark == (item.Model.Id, item.Operation)) && item.Budget is not null &&
                !unsafePlacements.Contains(FaultKey(item.Budget)) &&
                item.Profile?.Error is null && AttributionAllows(item.Endpoint, current, observed, item.Budget.AllowAttached) &&
                !(requestedBenchmark is null && current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient &&
                    item.Model.Instance is not null && item.Model.Id == catalog.Selected && item.CanColdLoad &&
                    item.Profile?.Measurements.Any(pass => pass.Pass is "reuse" or "warm") == true &&
                    !item.Profile.Measurements.Any(pass => pass.Pass == "cold")) &&
                (item.Model.Instance is not null || item.Endpoint.CanLoad && item.Model.Model.SupportsLoad));
            if (target is null)
                return;
            var protectSelected = requestedBenchmark is null && current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient &&
                target.Model.Id == catalog.Selected;
            requestedBenchmark = null;
            benchmarkStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            benchmarkDeployment = target.Model.Id;
            benchmarkPass = "Preparing";
            var cancellation = benchmarkStop.Token;
            benchmark = Task.Run(async () =>
            {
                try { await RunBenchmarkAsync(providers, target, protectSelected, cancellation); }
                finally { Signal(); }
            }, CancellationToken.None);
        }
    }

    private void SetBenchmarkReady(bool ready)
    {
        if (benchmarksReady != ready)
        {
            benchmarksReady = ready;
            Changed?.Invoke();
        }
    }

    private bool IsPending(Target item) => (item.Model.Online || item.Endpoint.CanLoad) &&
        (item.Profile?.Complete != true || item.Budget is null || unsafePlacements.Contains(FaultKey(item.Budget)));

    private void CatalogChanged(CatalogView catalog)
    {
        lock (sync)
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                SetBenchmarkReady(!Targets(catalog).Any(IsPending));
        Signal();
    }

    private async Task RunBenchmarkAsync(ProviderCatalog providers, Target target, bool protectSelected, CancellationToken cancellation)
    {
        var profile = target.Profile ?? new BenchmarkProfile(target.Model.Id, target.Configuration, target.Runtime, target.Operation,
            clock.GetUtcNow(), [], false, null);
        try
        {
            var model = target.Model;
            var needsCold = target.CanColdLoad && !profile.Measurements.Any(pass => pass.Pass == "cold");
            if (needsCold && target.Budget!.Load is { RamBytes: 0 } allowance && allowance.DeviceBytes.Values.All(value => value == 0))
                throw new ProviderFailure(409, "A peak memory allowance is required for the cold load benchmark.");
            if (needsCold && (!protectSelected || model.Instance is null))
            {
                var freshBaseline = true;
                if (model.Instance is not null)
                {
                    await providers.ManageAsync(model.Id, load: false, cancellation);
                    freshBaseline = await WaitForObservationAsync(cancellation);
                }
                BeginMeasurement("cold", model.EndpointId, freshBaseline);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await providers.ManageAsync(model.Id, load: true, cancellation, target.Operation);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                model = providers.View.Models.First(item => item.Id == model.Id);
                var runtime = model.Instance?.ResourceFingerprint ?? throw new ProviderFailure(502, "The benchmark load was not observed.");
                if (profile.Runtime != runtime)
                    profile = profile with { Runtime = runtime, Measurements = [] };
                await WaitForObservationAsync(cancellation);
                var pass = EndMeasurement(new(target.Operation, model.Context ?? 1), "cold", elapsed);
                profile = await SavePassAsync(profile, pass);
            }
            if (model.Instance is null)
            {
                await providers.ManageAsync(model.Id, load: true, cancellation, target.Operation);
                model = providers.View.Models.First(item => item.Id == model.Id);
            }
            if (model.Instance is not { } instance || !model.Model.Operations.Contains(target.Operation))
                throw new ProviderFailure(409, "This operation is not supported by the loaded model.");
            if (profile.Runtime != instance.ResourceFingerprint)
                profile = profile with { Runtime = instance.ResourceFingerprint, Measurements = [] };
            var plan = BenchmarkProtocol.Plan(model, target.Operation, target.Budget!.Workload);
            foreach (var (name, workload) in plan)
            {
                cancellation.ThrowIfCancellationRequested();
                if (profile.Measurements.Any(item => item.Pass == name))
                    continue;
                BeginMeasurement(name, model.EndpointId);
                var result = await providers.BenchmarkAsync(model.Id, workload, name, cancellation);
                var pass = EndMeasurement(workload, name, result.Elapsed) with
                {
                    PromptTokens = result.PromptTokens, OutputTokens = result.OutputTokens, FirstToken = result.FirstToken,
                    PromptMilliseconds = result.PromptMilliseconds, TokensPerSecond = result.TokensPerSecond,
                    CachedTokens = result.CachedTokens, ReusedCache = result.ReusedCache
                };
                profile = await SavePassAsync(profile, pass);
            }
            if (!target.CanColdLoad || profile.Measurements.Any(pass => pass.Pass == "cold"))
                await SaveProfileAsync(profile with { Complete = true, Updated = clock.GetUtcNow() });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                await SaveProfileAsync(profile with { Complete = false, Error = "The benchmark was interrupted. Retry to continue." });
        }
        catch (Exception error) when (error is ProviderFailure or IOException or UnauthorizedAccessException)
        {
            await SaveProfileAsync(profile with
            {
                Complete = false,
                Error = error is ProviderFailure provider ? provider.Message : "Mango could not read or save a benchmark profile."
            });
        }
        finally
        {
            lock (sync)
                measurement = null;
        }
    }

    private async Task<bool> WaitForObservationAsync(CancellationToken cancellation)
    {
        Task observation;
        lock (sync)
            observation = nextObservation.Task;
        try
        {
            await observation.WaitAsync(TimeSpan.FromSeconds(Math.Min(6, settings.Value.SampleSeconds + 1)), cancellation);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private void BeginMeasurement(string pass, Guid endpoint, bool freshBaseline = true)
    {
        lock (sync)
        {
            benchmarkPass = pass;
            measurement = new(freshBaseline ? observed : null, endpoint);
        }
    }

    private void ObserveBenchmark(ResourceObservation current)
    {
        if (measurement is not { Before: { } start } window)
            return;
        window.Samples++;
        window.Ram = Math.Max(window.Ram, start.FreeRam - current.FreeRam);
        foreach (var device in current.Devices.Where(item => !item.Integrated && item.UsedBytes.HasValue))
            if (start.Devices.FirstOrDefault(item => item.Id == device.Id)?.UsedBytes is { } before)
                window.Vram[device.Id] = Math.Max(window.Vram.GetValueOrDefault(device.Id), device.UsedBytes!.Value - before);
        if (current.Workers.GetValueOrDefault(window.Endpoint)?.PeakResidentBytes is { } peak)
            window.WorkerPeak = Math.Max(window.WorkerPeak ?? 0, peak);
    }

    private BenchmarkMeasurement EndMeasurement(Workload workload, string pass, TimeSpan elapsed)
    {
        lock (sync)
        {
            var window = measurement!;
            measurement = null;
            var usage = window.Samples > 0 ? new MemoryCost(window.Ram, window.Vram) : null;
            return new(workload, pass, null, null, elapsed, null, null, null, null, usage, window.WorkerPeak, window.Samples);
        }
    }

    private async Task<BenchmarkProfile> SavePassAsync(BenchmarkProfile profile, BenchmarkMeasurement pass)
    {
        profile = profile with
        {
            Updated = clock.GetUtcNow(), Measurements = profile.Measurements.Append(pass).ToArray(),
            Complete = false, Error = null
        };
        await SaveProfileAsync(profile);
        return profile;
    }

    private async Task SaveProfileAsync(BenchmarkProfile profile)
    {
        try
        {
            await store.UpdateAsync(data => data with
            {
                Profiles = data.Profiles.Where(item => item.Deployment != profile.Deployment || item.Operation != profile.Operation)
                    .Append(profile).ToArray()
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (sync)
                SetStatus(new(ContributionState.Fault, "Mango could not save benchmark evidence.", null));
        }
        Changed?.Invoke();
    }

    private static string? Validate(ResourceData data)
    {
        if (data.Budgets is not { Length: <= 1536 } || data.Budgets.Any(item => !ValidBudget(item)) ||
            data.Profiles is not { Length: <= 1536 } || data.Profiles.Any(item => item is null ||
                !Enum.IsDefined(item.Operation) || !Hash(item.Deployment) || !Hash(item.Configuration) ||
                item.Runtime is not { Length: 0 } && !Hash(item.Runtime) ||
                item.Measurements is not { Length: <= 8 } ||
                item.Measurements.Any(pass => pass is null || pass.ObservedIncrement is not null && !ValidCost(pass.ObservedIncrement))) ||
            data.Unsafe is not { Length: <= 1024 } || data.Unsafe.Any(item => !Hash(item)) ||
            data.Hold is { } hold && (hold.State is not (ContributionState.Fault or ContributionState.Cooldown or ContributionState.Withdrawing) ||
                string.IsNullOrWhiteSpace(hold.Reason) || hold.Reason.Length > 512 ||
                hold.Pending is { } pending && (pending.Length > ProviderLimits.MaxModels ||
                    pending.Any(item => item is null || item.Endpoint == Guid.Empty || !Hash(item.Deployment) ||
                        item.OwnedProcesses is not { Length: <= 256 } || item.OwnedProcesses.Any(pid => pid <= 0)))))
            return "The local resource profiles are invalid.";
        return null;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool ValidBudget(ResourceBudget? item) => item is not null && Hash(item.Deployment) &&
        Hash(item.Configuration) && (item.Runtime is null || Hash(item.Runtime)) && Enum.IsDefined(item.Workload.Operation) &&
        item.Workload.Units is >= 1 and <= ProviderLimits.MaxRequestBytes && item.Workload.Items is >= 1 and <= 128 &&
        item.Workload.MediaItems is >= 0 and <= 128 && item.ParallelSlots is >= 1 and <= 64 &&
        (item.Workload.Operation switch
        {
            InferenceOperation.Images => item.Workload is { Units: <= 4096 * 4096, Items: <= 10, MediaItems: 0 },
            InferenceOperation.Transcription => item.Workload is { Units: <= ProviderLimits.MaxAudioBytes, Items: 1, MediaItems: 0 },
            InferenceOperation.Speech => item.Workload is { Units: <= 4096, Items: 1, MediaItems: 0 },
            InferenceOperation.Chat => item.Workload is { Units: <= ProviderLimits.MaxContext, Items: <= 8 },
            InferenceOperation.SystemOne => item.Workload is { Units: <= ProviderLimits.MaxContext, Items: 1 },
            _ => item.Workload.Units <= ProviderLimits.MaxContext
        }) &&
        ValidCost(item.Load) && ValidCost(item.Execute);
    private static bool ValidCost(MemoryCost? cost) => cost is { RamBytes: >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB,
        DeviceBytes.Count: <= 32 } && cost.DeviceBytes.All(item => item.Key.Length <= 64 &&
            item.Value is >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB);
}
