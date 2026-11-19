using Mango.Host;
using Mango.Providers;
using System.Text.Json;

namespace Mango.Resources;

internal sealed record BudgetUpdate(string Deployment, InferenceOperation Operation, long Units, int Items, int MediaItems,
    long? LoadRamMiB, long? ExecuteRamMiB, Dictionary<string, long>? LoadVramMiB, Dictionary<string, long>? ExecuteVramMiB,
    bool AllowOverlap, int ParallelSlots, bool AllowAttached, string? ExpectedRevision = null);

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

    public ResourceBudget? Estimate(Deployment model, EndpointStatus endpoint, InferenceOperation operation)
    {
        lock (sync)
            return FindBudget(model, endpoint, operation);
    }

    // A cold routing bound is qualification evidence, never advertised loaded context.
    public long? QualifiedUnits(Deployment model, EndpointStatus endpoint, InferenceOperation operation)
    {
        lock (sync)
        {
            if (FindBudget(model, endpoint, operation) is { Estimated: false } budget)
                return budget.Workload.Units;
            var profile = MatchProfile(model.Id, operation, Configuration(model), model.Instance?.ResourceFingerprint);
            return profile is { Complete: true } ? profile.Measurements.Where(item => item.Workload.Operation == operation)
                .Select(item => (long?)item.Workload.Units).Max() : null;
        }
    }

    public (int Complete, int Partial) BenchmarkCounts(Deployment model)
    {
        lock (sync)
        {
            var configuration = Configuration(model);
            var complete = 0;
            var partial = 0;
            foreach (var operation in model.Model.Operations)
                if (MatchProfile(model.Id, operation, configuration, model.Instance?.ResourceFingerprint) is { } profile)
                {
                    if (profile.Complete)
                        complete++;
                    else
                        partial++;
                }
            return (complete, partial);
        }
    }

    public (double? PromptMilliseconds, double? CacheSavingsMilliseconds, double? GenerationTokensPerSecond) RouteTiming(
        Deployment model, InferenceOperation operation)
    {
        lock (sync)
        {
            var profile = MatchProfile(model.Id, operation, Configuration(model), model.Instance?.ResourceFingerprint);
            var cold = profile?.Measurements.Where(item => item.ReusedCache != true)
                .Select(item => item.PromptMilliseconds ?? item.FirstToken?.TotalMilliseconds).OfType<double>()
                .DefaultIfEmpty().Average();
            var warm = profile?.Measurements.Where(item => item.ReusedCache == true)
                .Select(item => item.PromptMilliseconds ?? item.FirstToken?.TotalMilliseconds).OfType<double>()
                .DefaultIfEmpty().Average();
            return (cold is > 0 ? cold : null, cold is > 0 && warm is > 0 && warm < cold ? cold - warm : null,
                GenerationRate(profile));
        }
    }

    private static double? GenerationRate(BenchmarkProfile? profile)
    {
        if (profile is not { Operation: InferenceOperation.Chat })
            return null;
        return profile.Speed?.TokensPerSecond is > 0 and < double.PositiveInfinity ? profile.Speed.TokensPerSecond :
            profile.Measurements.FirstOrDefault(pass => pass.Pass == "short" &&
                pass.TokensPerSecond is > 0 and < double.PositiveInfinity)?.TokensPerSecond;
    }

    private sealed record Target(Deployment Model, EndpointStatus Endpoint, InferenceOperation Operation, string Configuration,
        string Runtime, ResourceBudget? Budget, BenchmarkProfile? Profile)
    {
        public bool CanColdLoad => Endpoint.CanLoad && (Endpoint.CanUnload || Endpoint.Executable is not null) &&
            Model.Model.SupportsLoad && Model.Model.SupportsUnload;

        public bool HasLoadAllowance => Budget is { } budget &&
            (budget.Load.RamBytes > 0 || budget.Load.DeviceBytes.Values.Any(bytes => bytes > 0));
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
                identifier = target.Model.Alias ?? target.Model.Model.NativeId,
                target.Operation,
                Budget = target.Budget,
                QualificationConfiguration = target.Configuration + ":" + target.Model.Instance?.ResourceFingerprint,
                BudgetRevision = BudgetRevision(store.Value.Budgets.FirstOrDefault(item => item.Deployment == target.Model.Id &&
                    item.Workload.Operation == target.Operation), target.Configuration, target.Model.Instance?.ResourceFingerprint),
                Profile = target.Profile,
                GenerationTokensPerSecond = GenerationRate(target.Profile),
                Readiness = Readiness(target.Model, target.Endpoint, target.Operation, ProviderCatalog.ExclusiveBlocker(catalog, target.Endpoint)),
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
            if (benchmarkDeployment == update.Deployment && benchmark is { IsCompleted: false } ||
                reservations.Any(item => item.Deployment == update.Deployment && !item.Settled))
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
            lock (sync)
            {
                var model = providers.View.Models.FirstOrDefault(item => item.Id == update.Deployment);
                if (model is null || Configuration(model) != budget.Configuration || model.Instance?.ResourceFingerprint != budget.Runtime)
                    throw new ProviderFailure(409, "This deployment or its hardware changed. Review its current limits.", code: "configuration_changed");
                if (update.ExpectedRevision is not null && update.ExpectedRevision != BudgetRevision(previous, budget.Configuration, budget.Runtime))
                    throw new ProviderFailure(409, "These local limits changed elsewhere. Review their current values.", code: "conflict");
            }
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

    private static string BudgetRevision(ResourceBudget? budget, string configuration, string? runtime) =>
        ProviderIdentity.Hash(configuration, runtime, JsonSerializer.Serialize(budget, SnapshotFile<ResourceData>.Json));

    public async Task RetryBenchmarkAsync(string deployment, InferenceOperation operation, ProviderCatalog providers,
        CancellationToken cancellation)
    {
        var target = Targets(providers.View).FirstOrDefault(item => item.Model.Id == deployment && item.Operation == operation)
            ?? throw new ProviderFailure(404, "This deployment was not found.");
        if (target.Model.ServingDisabled)
            throw new ProviderFailure(409, "Enable serving for this model before benchmarking it.");
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
        hardwareObservation?.Hardware, hardwareObservation?.TotalRam.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private ResourceBudget? FindBudget(Deployment model, EndpointStatus endpoint, InferenceOperation operation)
    {
        var configuration = Configuration(model);
        var runtime = model.Instance?.ResourceFingerprint;
        var stored = store.Value.Budgets.FirstOrDefault(item => item.Deployment == model.Id &&
            item.Workload.Operation == operation && item.Configuration == configuration &&
            (item.Runtime is null || runtime is null || item.Runtime == runtime));
        stored ??= EstimateBudget(model, operation, configuration, runtime);
        if (stored is null) return null;
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

    private ResourceBudget EstimateBudget(Deployment model, InferenceOperation operation, string configuration, string? runtime,
        Workload? execution = null)
    {
        var hardware = hardwareObservation;

        var text = operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne;
        var context = Math.Clamp(model.Context ?? model.Model.TheoreticalContext ?? 4096, 1, ProviderLimits.MaxContext);
        var workload = execution ?? (operation switch
        {
            InferenceOperation.Embeddings => new Workload(operation, context, 16),
            InferenceOperation.SystemOne => new Workload(operation, context, 4, model.MediaTokensPerItem is null ? 0 : 1),
            InferenceOperation.Chat => new Workload(operation, context, 1, model.MediaTokensPerItem is null ? 0 : 1),
            InferenceOperation.Images => new Workload(operation, 1024 * 1024),
            InferenceOperation.Transcription => new Workload(operation, ProviderLimits.MaxAudioBytes),
            InferenceOperation.Speech => new Workload(operation, 4096),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        // Reserve working memory in addition to resident weights already counted by Windows.
        var weightBytes = model.Model.ArtifactBytes.GetValueOrDefault();
        var limit = (long)LocalSettings.MaximumReserveMiB * MiB;
        var inputBytes = text ? workload.Units * workload.Items * (operation == InferenceOperation.Embeddings ? 32 * 1024L : 128 * 1024L)
            : workload.Units * (operation == InferenceOperation.Images ? 64 : 4);
        if (model.Model.Origin == ModelOrigin.LmLink)
            return new(model.Id, configuration, runtime, workload, MemoryCost.Empty, new(128 * MiB, []),
                AllowOverlap: false, Estimated: true);
        var executeBytes = Math.Min(limit, 256 * MiB + Math.Max(inputBytes, weightBytes / 8));
        var loadBytes = weightBytes > 0 && weightBytes <= limit - executeBytes ? weightBytes + executeBytes : 0;
        MemoryCost Cost(long bytes) => new(bytes, (hardware?.Devices ?? []).Where(device => !device.Integrated && device.TotalBytes is not null)
            .ToDictionary(device => device.Id, _ => bytes));
        return new(model.Id, configuration, runtime, workload, Cost(loadBytes), Cost(executeBytes),
            AllowOverlap: false, Estimated: true);
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
            var targets = Targets(catalog);
            SetBenchmarkReady(current.BenchmarkPolicy == BenchmarkPolicy.Never || !targets.Any(IsPending));
            if (benchmark is not null || status.State != ContributionState.Contributor ||
                reservations.Any(item => !item.Settled) || observed is null)
                return;
            var now = clock.GetUtcNow();
            if (requestedBenchmark is null && (current.BenchmarkPolicy == BenchmarkPolicy.Never ||
                current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient && now - lastClient < BenchmarkIdle))
                return;
            var candidates = targets.Where(item => !item.Model.Yolo && !item.Model.ServingDisabled &&
                item.Budget is not null && !unsafePlacements.Contains(FaultKey(item.Budget)) &&
                AttributionAllows(item.Endpoint, current, observed, item.Budget.AllowAttached) &&
                !item.Model.Busy && item.Model.Instance?.Busy != true && ProviderCatalog.ExclusiveBlocker(catalog, item.Endpoint) is null)
                .ToArray();
            var target = requestedBenchmark is null ? candidates.FirstOrDefault(item =>
                item.Operation == InferenceOperation.Chat && item.Model.Eligible &&
                item.Model.Context >= BenchmarkProtocol.SpeedContext && item.Budget!.Workload.Units >= BenchmarkProtocol.SpeedContext &&
                (item.Profile?.Speed is null || item.Profile.Speed.Error is not null && now - item.Profile.Speed.At >= BenchmarkIdle)) : null;
            var speedOnly = target is not null;
            var convenient = requestedBenchmark is null && current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient;
            target ??= candidates.FirstOrDefault(item => IsPending(item) &&
                (requestedBenchmark is null || requestedBenchmark == (item.Model.Id, item.Operation)) &&
                (item.Profile?.Error is null || convenient && now - item.Profile.Updated >= BenchmarkIdle) &&
                !(convenient && PreserveLoadedBenchmark(item, catalog) && item.CanColdLoad &&
                    item.Profile?.Measurements.Any(pass => pass.Pass is "reuse" or "warm") == true &&
                    !item.Profile.Measurements.Any(pass => pass.Pass == "cold")) &&
                (item.Model.Instance is not null || item.Endpoint.CanLoad && item.Model.Model.SupportsLoad));
            if (target is null)
                return;
            var protectSelected = convenient && PreserveLoadedBenchmark(target, catalog);
            requestedBenchmark = null;
            benchmarkStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            benchmarkDeployment = target.Model.Id;
            benchmarkPass = speedOnly ? "Generation speed" : "Preparing";
            var cancellation = benchmarkStop.Token;
            benchmark = Task.Run(async () =>
            {
                try
                {
                    if (speedOnly) await RunSpeedBenchmarkAsync(providers, target, cancellation);
                    else await RunBenchmarkAsync(providers, target, protectSelected, cancellation);
                }
                finally { Signal(); }
            }, CancellationToken.None);
        }
    }

    // LM Studio load defaults do not reproduce a live instance's context, GPU placement or slots.
    // Background qualification may measure it warm, but must not replace that configuration.
    private static bool PreserveLoadedBenchmark(Target target, CatalogView catalog) =>
        target.Model.Instance is not null &&
        (!target.HasLoadAllowance || target.Model.Id == catalog.Selected || target.Endpoint.Profile == ProviderProfile.LmStudio);

    private void SetBenchmarkReady(bool ready)
    {
        if (benchmarksReady != ready)
        {
            benchmarksReady = ready;
            Changed?.Invoke();
        }
    }

    private bool IsPending(Target item) => !item.Model.Yolo && !item.Model.ServingDisabled &&
        (item.Model.Online && item.Model.Instance is not null || item.Endpoint.CanLoad && item.Model.Model.SupportsLoad) &&
        (item.Profile?.Complete != true || item.Budget is null || unsafePlacements.Contains(FaultKey(item.Budget)));

    private void CatalogChanged(CatalogView catalog)
    {
        lock (sync)
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                SetBenchmarkReady(!Targets(catalog).Any(IsPending));
        Signal();
    }

    private async Task RunSpeedBenchmarkAsync(ProviderCatalog providers, Target target, CancellationToken cancellation)
    {
        var profile = ProfileFor(target);
        double? rate = null;
        string? failure = null;
        try
        {
            var result = await providers.BenchmarkAsync(target.Model.Id, new(InferenceOperation.Chat, BenchmarkProtocol.SpeedContext),
                BenchmarkProtocol.SpeedPass, target.Runtime, cancellation);
            rate = result.TokensPerSecond;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error) when (error is ProviderFailure or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            failure = error is ProviderFailure provider ? provider.Message : "Mango could not finish the provider speed sample.";
        }
        await SaveProfileAsync(profile with { Speed = new(clock.GetUtcNow(), rate, failure) });
    }

    private BenchmarkProfile ProfileFor(Target target) =>
        target.Profile ?? new BenchmarkProfile(target.Model.Id, target.Configuration, target.Runtime, target.Operation,
            clock.GetUtcNow(), [], false, null);

    private async Task RunBenchmarkAsync(ProviderCatalog providers, Target target, bool protectSelected, CancellationToken cancellation)
    {
        var profile = ProfileFor(target);
        try
        {
            var model = target.Model;
            var needsCold = target.CanColdLoad && !profile.Measurements.Any(pass => pass.Pass == "cold");
            if (needsCold && (!protectSelected || model.Instance is null))
            {
                if (!target.HasLoadAllowance)
                    throw new ProviderFailure(409, "A peak memory allowance is required for the cold load benchmark.");
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
                    profile = profile with { Runtime = runtime, Measurements = [], Speed = null };
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
                profile = profile with { Runtime = instance.ResourceFingerprint, Measurements = [], Speed = null };
            var plan = BenchmarkProtocol.Plan(model, target.Operation, target.Budget!.Workload);
            foreach (var (name, workload) in plan)
            {
                cancellation.ThrowIfCancellationRequested();
                if (profile.Measurements.Any(item => item.Pass == name))
                    continue;
                BeginMeasurement(name, model.EndpointId);
                var result = await providers.BenchmarkAsync(model.Id, workload, name, instance.ResourceFingerprint, cancellation);
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
        catch (Exception error) when (error is ProviderFailure or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            await SaveProfileAsync(profile with
            {
                Complete = false, Updated = clock.GetUtcNow(),
                Error = error is ProviderFailure provider ? provider.Message : "Mango could not read or save a benchmark profile."
            });
        }
        finally
        {
            lock (sync)
                measurement = null;
        }
    }

    internal async Task<bool> WaitForObservationAsync(CancellationToken cancellation)
    {
        Task observation;
        lock (sync)
        {
            observation = nextObservation.Task;
            Volatile.Write(ref settlementSample, 1);
        }
        Signal();
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
                !Enum.IsDefined(hold.Cause) || string.IsNullOrWhiteSpace(hold.Reason) || hold.Reason.Length > 512 ||
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
            InferenceOperation.SystemOne => item.Workload.Units <= ProviderLimits.MaxContext,
            _ => item.Workload.Units <= ProviderLimits.MaxContext
        }) &&
        ValidCost(item.Load) && ValidCost(item.Execute);
    private static bool ValidCost(MemoryCost? cost) => cost is { RamBytes: >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB,
        DeviceBytes.Count: <= 32 } && cost.DeviceBytes.All(item => item.Key.Length <= 64 &&
            item.Value is >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB);
}
