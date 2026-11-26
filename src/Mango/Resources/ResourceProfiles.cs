using Mango.Host;
using Mango.Providers;
using System.Text.Json;

namespace Mango.Resources;

internal sealed record BudgetUpdate(string Deployment, InferenceOperation Operation, long Units, int Items, int MediaItems,
    long? LoadRamMiB, long? ExecuteRamMiB, Dictionary<string, long>? LoadVramMiB, Dictionary<string, long>? ExecuteVramMiB,
    bool AllowOverlap, int ParallelSlots, bool AllowAttached, string? ExpectedRevision = null, bool LoadOnly = false);

internal sealed partial class ResourceProtection
{
    private bool benchmarksReady;
    private string? benchmarkDeployment;
    private string? benchmarkPass;
    private const string MissingColdAllowance = "A peak memory allowance is required for the cold load benchmark.";
    private const string MissingGenerationRate = "The provider did not expose a reliable generation rate.";
    private sealed record ForcedBenchmark(string Deployment, InferenceOperation Operation, bool SampleOnly, DateTimeOffset RequestedAt);
    private ForcedBenchmark? requestedBenchmark;
    private readonly Queue<Target> missingBenchmarks = new();
    private bool preparingForcedBenchmark;
    private bool runningForcedBenchmark;
    private string? forcedBenchmarkError;
    private MeasurementWindow? measurement;

    internal const string ForcedBenchmarkWait = "Waiting for a forced benchmark to finish.";

    internal bool ForcedBenchmarkHoldsServing
    {
        get { lock (sync) return requestedBenchmark is not null || runningForcedBenchmark || missingBenchmarks.Count != 0; }
    }

    internal bool BenchmarkOccupiesSlot
    {
        get { lock (sync) return benchmark is { IsCompleted: false } || ForcedBenchmarkHoldsServing; }
    }

    internal bool ForcedBenchmarkDisplacesWork
    {
        get { lock (sync) return ForcedBenchmarkHoldsServing && !preparingForcedBenchmark; }
    }

    public bool ServingAllowed
    {
        get
        {
            lock (sync)
                return !startupInspectionPending && status.State == ContributionState.Contributor && settings.Value.Role == LocalRole.Contributor &&
                    Volatile.Read(ref stopping) == 0;
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

    public (double? PromptMilliseconds, double? CacheSavingsMilliseconds, double? GenerationTokensPerSecond,
        bool? ProviderGenerationRate) RouteTiming(
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
                GenerationRate(profile), profile?.Speed is { TokensPerSecond: > 0 } speed ? speed.ProviderGenerationRate : null);
        }
    }

    private static double? GenerationRate(BenchmarkProfile? profile)
    {
        if (profile is not { Operation: InferenceOperation.Chat } || profile.Speed?.Error == MissingGenerationRate)
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

        public bool NativeBenchmark => CanUseNativeBenchmark(Model, Endpoint);
    }
    private sealed class MeasurementWindow(ResourceObservation? before, Guid endpoint)
    {
        public readonly ResourceObservation? Before = before;
        public readonly Guid Endpoint = endpoint;
        public long Ram;
        public readonly Dictionary<string, long> Vram = [];
        public MemoryCost? LastIncrement;
        public long? WorkerPeak;
        public int Samples;
    }

    public object View(ProviderCatalog providers)
    {
        var catalog = providers.View;
        lock (sync)
        {
            var targets = Targets(catalog);
            var models = targets.Select(target => new
            {
                Id = target.Model.Id,
                identifier = target.Model.Alias ?? target.Model.Model.NativeId,
                target.Operation,
                Budget = target.Budget,
                LoadOverride = target.Budget is { Estimated: false } budgetOverride ? OwnerPeak(budgetOverride) : null,
                QualificationConfiguration = target.Configuration + ":" + target.Model.Instance?.ResourceFingerprint,
                BudgetRevision = BudgetRevision(store.Value.Budgets.FirstOrDefault(item => item.Deployment == target.Model.Id &&
                    item.Workload.Operation == target.Operation), target.Configuration, target.Model.Instance?.ResourceFingerprint),
                Profile = target.Profile,
                GenerationTokensPerSecond = GenerationRate(target.Profile),
                ProviderGenerationRate = target.Profile?.Speed is { TokensPerSecond: > 0 } speed ? speed.ProviderGenerationRate : null,
                Readiness = Readiness(target.Model, target.Endpoint, target.Operation, ProviderCatalog.ExclusiveBlocker(catalog, target.Endpoint),
                    modeSwitch: ProviderCatalog.NeedsModeSwitch(catalog, target.Model)),
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
                Forced = ForcedBenchmarkHoldsServing, ForcedBenchmarkError = forcedBenchmarkError,
                MissingBenchmarks = targets.Count(CanBenchmark),
                Models = models, RecoveryWarning = store.RecoveryWarning
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
            if (update.LoadOnly)
            {
                if (update.LoadRamMiB is < 0 or > LocalSettings.MaximumReserveMiB || update.LoadVramMiB?.Any(pair =>
                    pair.Value is < 0 or > LocalSettings.MaximumReserveMiB ||
                    !observed.Devices.Any(device => !device.Integrated && device.Id == pair.Key)) == true)
                    throw new ProviderFailure(400, "Enter a peak in MiB for a known local memory pool.");
                var peak = new LoadPeak(update.LoadRamMiB * MiB,
                    update.LoadVramMiB?.ToDictionary(pair => pair.Key, pair => pair.Value * MiB) ?? []);
                budget = target.Budget! with { Runtime = target.Model.Instance?.ResourceFingerprint, LoadOverride = peak,
                    Estimated = false, Load = new(peak.RamBytes ?? 0, peak.DeviceBytes) };
            }
            else budget = new(target.Model.Id, target.Configuration, target.Model.Instance?.ResourceFingerprint,
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
        CancellationToken cancellation, bool sampleOnly = false)
    {
        var target = Targets(providers.View).FirstOrDefault(item => item.Model.Id == deployment && item.Operation == operation)
            ?? throw new ProviderFailure(404, "This deployment was not found.");
        if (target.Model.ServingDisabled)
            throw new ProviderFailure(409, "Enable serving for this model before benchmarking it.");
        if (target.Budget is not { } budget)
            throw new ProviderFailure(409, "Set a local memory limit before benchmarking this deployment.");
        var request = new ForcedBenchmark(deployment, operation, sampleOnly, clock.GetUtcNow());
        lock (sync)
        {
            if (settings.Value.Role != LocalRole.Contributor)
                throw new ProviderFailure(409, "Turn on contribution before forcing a benchmark.");
            if (observed is null)
                throw new ProviderFailure(503, "Wait for current local resource observations before forcing a benchmark.");
            if (ForcedBenchmarkHoldsServing)
                throw new ProviderFailure(409, "Wait for the existing forced benchmark to settle.");
            if (sampleOnly && target.Model.Instance is null && (!target.CanColdLoad ||
                !target.HasLoadAllowance && !target.NativeBenchmark))
                throw new ProviderFailure(409, "This provider must support managed load and unload before Mango can sample the cold model.");
            preparingForcedBenchmark = true;
            requestedBenchmark = request;
            forcedBenchmarkError = null;
        }
        try
        {
            if (!sampleOnly)
                await store.UpdateAsync(data => data with
                {
                    Unsafe = data.Unsafe.Where(item => item != FaultKey(budget)).ToArray(),
                    Profiles = data.Profiles.Where(item => item.Deployment != deployment || item.Operation != operation)
                        .Concat(target.Profile is { } profile && profile.Measurements.Any(pass => pass.Pass == "cold")
                            ? [profile with { Complete = false, Error = null, Speed = null,
                                Measurements = profile.Measurements.Where(pass => pass.Pass == "cold").ToArray() }]
                            : []).ToArray()
                }, cancellation);
            lock (sync)
            {
                if (!sampleOnly)
                {
                    unsafePlacements.Remove(FaultKey(budget));
                    if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                        SetBenchmarkReady(false);
                }
                DisplaceBenchmarkWork();
            }
        }
        catch
        {
            lock (sync)
                requestedBenchmark = null;
            throw;
        }
        finally
        {
            lock (sync)
                preparingForcedBenchmark = false;
            Changed?.Invoke();
            Signal();
        }
    }

    private void DisplaceBenchmarkWork()
    {
        RequestCancel(benchmarkStop);
        foreach (var lease in reservations.Where(item => item.Running && item.Work != ResourceWork.Benchmark))
            RequestCancel(lease.Stop);
    }

    private static bool CanBenchmark(Target target) =>
        target.Budget is not null && !target.Model.ServingDisabled &&
        (target.Model.Instance is not null ||
            !target.Model.Yolo && target.CanColdLoad && (target.HasLoadAllowance || target.NativeBenchmark)) &&
        (target.Profile?.Speed is not { Error: null, Elapsed: not null } ||
            target.Operation == InferenceOperation.Chat && GenerationRate(target.Profile) is null);

    public int BenchmarkMissing(ProviderCatalog providers)
    {
        var catalog = providers.View;
        lock (sync)
        {
            if (settings.Value.Role != LocalRole.Contributor || observed is null)
                throw new ProviderFailure(409, "Turn on contribution and wait for local observations before benchmarking.");
            if (ForcedBenchmarkHoldsServing)
                throw new ProviderFailure(409, "Wait for the existing forced benchmark to settle.");
            foreach (var target in Targets(catalog).Where(CanBenchmark))
                missingBenchmarks.Enqueue(target);
            if (missingBenchmarks.Count != 0)
            {
                forcedBenchmarkError = null;
                DisplaceBenchmarkWork();
                Signal();
            }
            return missingBenchmarks.Count;
        }
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
        if (!stored.Estimated) return stored;
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

    private MemoryCost? CheckedLoadCost(Deployment model, InferenceOperation operation)
    {
        var configuration = Configuration(model);
        var runtime = model.Instance?.ResourceFingerprint;
        var owner = store.Value.Budgets.FirstOrDefault(item => item.Deployment == model.Id &&
            item.Configuration == configuration && item.Workload.Operation == operation &&
            (runtime is null || item.Runtime is null || item.Runtime == runtime));
        var overrides = owner is null ? null : OwnerPeak(owner);
        var native = model.Options.ServedContext == model.Model.NativePeakContext ? model.Model.NativeLoadPeak : null;
        var profile = MatchProfile(model.Id, operation, configuration, runtime);
        var measured = unsafePlacements.Contains(ProviderIdentity.Hash(model.Id, configuration, operation.ToString())) ? null :
            MeasuredLoadCost(profile, LoadIdentity(model));
        if ((overrides?.RamBytes ?? native?.RamBytes ?? measured?.RamBytes) is not { } ramBytes) return null;
        var devices = measured?.DeviceBytes.ToDictionary() ?? [];
        foreach (var pair in native?.DeviceBytes ?? []) devices[pair.Key] = pair.Value;
        foreach (var pair in overrides?.DeviceBytes ?? []) devices[pair.Key] = pair.Value;
        return new(ramBytes, devices);
    }

    private static string LoadIdentity(Deployment model) => ProviderIdentity.Hash(model.Configuration, model.Version,
        model.Model.ManagementId, model.Model.Variant, model.Model.Format,
        model.Model.ArtifactBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static MemoryCost? MeasuredLoadCost(BenchmarkProfile? profile, string identity)
    {
        var cold = profile?.Measurements.LastOrDefault(pass => pass.Pass == "cold" && pass.LoadIdentity == identity);
        if (cold is not { Samples: > 0, ObservedIncrement: { } peak, SettledIncrement: { } resident }) return null;
        foreach (var pass in profile!.Measurements)
        {
            if (pass.Pass == "cold" || pass.LoadIdentity != identity || pass.ObservedIncrement is not { } warm) continue;
            // Warm increments start with the model resident. The cold peak already includes its residency.
            peak = new(Math.Max(peak.RamBytes, resident.RamBytes + warm.RamBytes),
                peak.DeviceBytes.Keys.Intersect(resident.DeviceBytes.Keys).Intersect(warm.DeviceBytes.Keys)
                    .ToDictionary(id => id, id => Math.Max(peak.DeviceBytes[id], resident.DeviceBytes[id] + warm.DeviceBytes[id])));
        }
        return peak;
    }

    private static LoadPeak OwnerPeak(ResourceBudget budget) =>
        budget.LoadOverride ?? new(budget.Load.RamBytes, budget.Load.DeviceBytes);

    private ResourceBudget EstimateBudget(Deployment model, InferenceOperation operation, string configuration, string? runtime)
    {
        var context = Math.Clamp(model.Context ?? model.Model.TheoreticalContext ?? 4096, 1, ProviderLimits.MaxContext);
        var workload = operation switch
        {
            InferenceOperation.Embeddings => new Workload(operation, context, 16),
            InferenceOperation.SystemOne => new Workload(operation, context, 4, model.MediaTokensPerItem is null ? 0 : 1),
            InferenceOperation.Chat => new Workload(operation, context, 1, model.MediaTokensPerItem is null ? 0 : 1),
            InferenceOperation.Images => new Workload(operation, 1024 * 1024),
            InferenceOperation.Transcription => new Workload(operation, ProviderLimits.MaxAudioBytes),
            InferenceOperation.Speech => new Workload(operation, 4096),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return new(model.Id, configuration, runtime, workload, MemoryCost.Empty, MemoryCost.Empty,
            AllowOverlap: false, Estimated: true);
    }

    private static MemoryCost Maximum(MemoryCost first, MemoryCost second) => new(Math.Max(first.RamBytes, second.RamBytes),
        first.DeviceBytes.Keys.Concat(second.DeviceBytes.Keys).Distinct().ToDictionary(id => id,
            id => Math.Max(first.DeviceBytes.GetValueOrDefault(id), second.DeviceBytes.GetValueOrDefault(id))));

    private BenchmarkProfile? MatchProfile(string id, InferenceOperation operation, string configuration, string? runtime)
    {
        var profile = store.Value.Profiles.FirstOrDefault(item => item.Deployment == id && item.Operation == operation &&
            item.Configuration == configuration && (runtime is null || item.Runtime == runtime));
        if (profile?.Speed is { ProviderGenerationRate: false, TokensPerSecond: > 0 } speed &&
            BenchmarkProtocol.StreamLooksBuffered(speed.FirstToken, speed.Elapsed))
            profile = profile with { Speed = speed with { TokensPerSecond = null, Error = MissingGenerationRate } };
        return profile is { Error: MissingColdAllowance } &&
            profile.Measurements.Any(pass => pass.Pass == "cold" && pass.ObservedIncrement is not null)
            ? profile with { Error = null } : profile;
    }

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
                if (runningForcedBenchmark)
                {
                    runningForcedBenchmark = false;
                    Changed?.Invoke();
                }
            }
            var catalog = providers.View;
            var targets = Targets(catalog);
            SetBenchmarkReady(current.BenchmarkPolicy == BenchmarkPolicy.Never || !targets.Any(IsPending));
            if (preparingForcedBenchmark)
                return;
            if (benchmark is null && requestedBenchmark is null && missingBenchmarks.TryDequeue(out var next))
                requestedBenchmark = new(next.Model.Id, next.Operation, SampleOnly: true, clock.GetUtcNow());
            if (benchmark is not null || reservations.Any(item => !item.Settled && (item.Running || !item.NativeBenchmark)))
            {
                if (requestedBenchmark is { } pending && clock.GetUtcNow() - pending.RequestedAt >= ProviderLimits.LifecycleTimeout)
                    FailForcedBenchmark("Mango could not confirm that the previous provider operation stopped.");
                return;
            }
            if (current.Role != LocalRole.Contributor)
            {
                missingBenchmarks.Clear();
                FailForcedBenchmark("Contribution is paused.");
                return;
            }
            if (observed is null)
                return;
            var now = clock.GetUtcNow();
            if (requestedBenchmark is null && (current.BenchmarkPolicy == BenchmarkPolicy.Never ||
                current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient && now - lastClient < BenchmarkIdle))
                return;
            var candidates = targets.Where(item => !item.Model.ServingDisabled && !item.Model.Busy &&
                item.Model.Instance?.Busy != true && !providers.HasUnsettledWork(item.Endpoint) &&
                ProviderCatalog.ExclusiveBlocker(catalog, item.Endpoint) is null).ToArray();
            bool Protected(Target item) => item.Budget is { } budget &&
                status.State == ContributionState.Contributor &&
                (!unsafePlacements.Contains(FaultKey(budget)) || item.NativeBenchmark && requestedBenchmark is not null);

            var forced = requestedBenchmark;
            bool Matches(Target item) => forced is null ||
                (forced.Deployment, forced.Operation) == (item.Model.Id, item.Operation);
            var target = candidates.FirstOrDefault(item =>
                Matches(item) && (forced is null || forced.SampleOnly || item.Model.Yolo) &&
                CanSample(item.Model, item.Operation) &&
                (item.Model.Yolo ? IsTextBenchmark(item.Operation) && ServingAllowed :
                    Protected(item) && Covers(item.Budget!.Workload, BenchmarkProtocol.SampleWorkload(item.Operation))) &&
                (forced is not null || item.Profile?.Speed is null || item.Profile.Speed.Error is not null &&
                    item.Profile.Speed.Attempts < 2 && now - item.Profile.Speed.At >= BenchmarkIdle));
            var speedOnly = target is not null;
            var convenient = forced is null && current.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient;
            target ??= candidates.FirstOrDefault(item => Matches(item) && Protected(item) &&
                (forced?.SampleOnly == true ? !item.Model.Yolo : IsPending(item)) &&
                (forced is not null || item.Profile?.Error is null ||
                    item.NativeBenchmark && item.Profile.Error == MissingColdAllowance) &&
                !(convenient && PreserveLoadedBenchmark(item, catalog) && item.CanColdLoad &&
                    item.Profile?.Measurements.Any(pass => pass.Pass is "reuse" or "warm") == true &&
                    !item.Profile.Measurements.Any(pass => pass.Pass == "cold")) &&
                (item.Model.Instance is not null || item.Endpoint.CanLoad && item.Model.Model.SupportsLoad));
            if (target is null)
            {
                FailForcedBenchmark("The deployment is unavailable for benchmarking. Check its provider state and local resource limits.");
                return;
            }
            var protectSelected = forced?.SampleOnly == true || convenient && PreserveLoadedBenchmark(target, catalog);
            requestedBenchmark = null;
            if (forced is not null)
                forcedBenchmarkError = null;
            runningForcedBenchmark = forced is not null;
            benchmarkStop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            benchmarkDeployment = target.Model.Id;
            benchmarkPass = speedOnly ? "Warm sample" : "Preparing";
            var cancellation = benchmarkStop.Token;
            benchmark = Task.Run(async () =>
            {
                try
                {
                    if (speedOnly) await RunSpeedBenchmarkAsync(providers, target.Model, ProfileFor(target), cancellation);
                    else await RunBenchmarkAsync(providers, target, protectSelected, forced?.SampleOnly == true, cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (sync)
                        if (runningForcedBenchmark)
                            forcedBenchmarkError = status.Reason ?? "The forced benchmark was interrupted.";
                }
                finally { Signal(); }
            }, CancellationToken.None);
        }
    }

    private void FailForcedBenchmark(string reason)
    {
        if (requestedBenchmark is null)
            return;
        requestedBenchmark = null;
        forcedBenchmarkError = reason;
        Changed?.Invoke();
        if (missingBenchmarks.Count != 0)
            Signal();
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

    private static bool IsTextBenchmark(InferenceOperation operation) =>
        operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne;

    private static bool CanSample(Deployment model, InferenceOperation operation) =>
        model.Eligible && (!IsTextBenchmark(operation) || model.Context >= BenchmarkProtocol.SpeedContext);

    private async Task<BenchmarkProfile> RunSpeedBenchmarkAsync(ProviderCatalog providers, Deployment model,
        BenchmarkProfile profile, CancellationToken cancellation)
    {
        BenchmarkResult? result = null;
        string? failure = null;
        try
        {
            lock (sync)
                benchmarkPass = "Warm sample";
            result = await providers.BenchmarkAsync(model.Id, BenchmarkProtocol.SampleWorkload(profile.Operation),
                BenchmarkProtocol.SpeedPass, profile.Runtime, cancellation);
        }
        catch (Exception error) when (error is ProviderFailure or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            failure = error is ProviderFailure provider ? provider.Message : "Mango could not finish the provider speed sample.";
        }
        if (result is { TokensPerSecond: null } && profile.Operation == InferenceOperation.Chat)
            failure = MissingGenerationRate;
        if (failure is not null)
        {
            lock (sync)
                if (runningForcedBenchmark)
                    forcedBenchmarkError = failure;
            if (runningForcedBenchmark && profile.Speed is { Error: null })
                return profile;
        }
        profile = profile with
        {
            Speed = new(clock.GetUtcNow(), result?.TokensPerSecond, failure, result?.Elapsed, result?.PromptTokens,
                Math.Min(2, (profile.Speed?.Attempts ?? 0) + 1), result?.ProviderGenerationRate,
                result?.FirstToken, result?.OutputTokens)
        };
        await SaveProfileAsync(profile);
        return profile;
    }

    private BenchmarkProfile ProfileFor(Target target) =>
        target.Profile ?? new BenchmarkProfile(target.Model.Id, target.Configuration, target.Runtime, target.Operation,
            clock.GetUtcNow(), [], false, null);

    private async Task RunBenchmarkAsync(ProviderCatalog providers, Target target, bool protectSelected, bool sampleOnly,
        CancellationToken cancellation)
    {
        var profile = ProfileFor(target);
        var model = target.Model;
        var openedForBenchmark = false;
        async Task LoadAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(ProviderLimits.LifecycleTimeout);
            try { await providers.ManageAsync(model.Id, load: true, deadline.Token, benchmark: target.Operation); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw deadline.IsCancellationRequested
                    ? new ProviderFailure(504, "The benchmark model did not load within the time limit.")
                    : new ProviderFailure(503, "The benchmark model load was interrupted.");
            }
            openedForBenchmark = target.Model.Instance is null;
            model = providers.View.Models.First(item => item.Id == model.Id);
        }

        try
        {
            if (model.Instance is null && (target.HasLoadAllowance || target.NativeBenchmark))
                await ReleaseBenchmarkSlotAsync(providers, target, cancellation);
            var needsCold = target.CanColdLoad && (model.Instance is null || !profile.Measurements.Any(pass => pass.Pass == "cold"));
            if (needsCold && (!protectSelected || model.Instance is null))
            {
                if (!target.HasLoadAllowance && !target.NativeBenchmark)
                    throw new ProviderFailure(409, MissingColdAllowance);
                var freshBaseline = true;
                if (model.Instance is not null)
                {
                    await providers.ManageAsync(model.Id, load: false, cancellation, target.Operation);
                    freshBaseline = await WaitForObservationAsync(cancellation);
                }
                BeginMeasurement("cold", model.EndpointId, freshBaseline);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await LoadAsync();
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                var runtime = model.Instance?.ResourceFingerprint ?? throw new ProviderFailure(502, "The benchmark load was not observed.");
                if (profile.Runtime != runtime)
                    profile = profile with { Runtime = runtime, Measurements = [], Speed = null };
                await WaitForObservationAsync(cancellation);
                var pass = EndMeasurement(new(target.Operation, model.Context ?? 1), "cold", elapsed) with { LoadIdentity = LoadIdentity(model) };
                profile = await SavePassAsync(profile, pass, preserveCompletion: sampleOnly);
            }
            if (model.Instance is null)
                await LoadAsync();
            if (model.Instance is not { } instance || !model.Model.Operations.Contains(target.Operation))
                throw new ProviderFailure(409, "This operation is not supported by the loaded model.");
            if (profile.Runtime != instance.ResourceFingerprint)
                profile = profile with { Runtime = instance.ResourceFingerprint, Measurements = [], Speed = null };
            if ((sampleOnly || openedForBenchmark && profile.Speed is null) && CanSample(model, target.Operation) &&
                Covers(target.Budget!.Workload, BenchmarkProtocol.SampleWorkload(target.Operation)))
                profile = await RunSpeedBenchmarkAsync(providers, model, profile, cancellation);
            if (sampleOnly)
            {
                if (profile.Speed is null)
                    throw new ProviderFailure(409, "The model's current context or workload limit is too small for the sample.");
                return;
            }
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
                    LoadIdentity = LoadIdentity(model), PromptTokens = result.PromptTokens, OutputTokens = result.OutputTokens, FirstToken = result.FirstToken,
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
            if (runningForcedBenchmark)
                throw;
            string? safetyFailure;
            lock (sync)
                safetyFailure = withdrawalCause == WithdrawalCause.Safety &&
                    status.State is (ContributionState.Withdrawing or ContributionState.Fault) ? status.Reason : null;
            if (safetyFailure is not null || settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now)
                await SaveProfileAsync(profile with { Complete = false, Updated = clock.GetUtcNow(),
                    Error = safetyFailure ?? "The benchmark was interrupted. Retry to continue." });
        }
        catch (Exception error) when (error is ProviderFailure or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            var reason = error is ProviderFailure provider ? provider.Message : "Mango could not read or save a benchmark profile.";
            lock (sync)
                if (runningForcedBenchmark)
                    forcedBenchmarkError = reason;
            if (!sampleOnly)
                await SaveProfileAsync(profile with { Complete = false, Updated = clock.GetUtcNow(), Error = reason });
            else if (profile.Speed is not { Error: null })
                await SaveProfileAsync(profile with { Speed = new(clock.GetUtcNow(), null, reason) });
        }
        finally
        {
            lock (sync)
                measurement = null;
            if (openedForBenchmark && target.CanColdLoad &&
                providers.View.Models.FirstOrDefault(item => item.Id == model.Id) is { Yolo: false } current &&
                current.Instance is { } loaded && loaded.Fingerprint == model.Instance?.Fingerprint &&
                (target.Endpoint.ExclusiveHost || !current.Busy && !loaded.Busy))
            {
                // A client can cancel the benchmark after the runtime has loaded.
                using var cleanup = new CancellationTokenSource(ProviderLimits.LifecycleTimeout);
                try
                {
                    await providers.ManageAsync(model.Id, load: false, cleanup.Token, benchmark: target.Operation);
                    if (!cancellation.IsCancellationRequested)
                        await WaitForObservationAsync(cleanup.Token);
                }
                catch (Exception error) when (error is ProviderFailure or OperationCanceledException) { }
            }
        }
    }

    private async Task ReleaseBenchmarkSlotAsync(ProviderCatalog providers, Target target, CancellationToken cancellation)
    {
        if (target.Endpoint.MaxLoadedModels is not > 0 || target.Endpoint.Mode != ProviderMode.Managed)
            return;
        var view = providers.View;
        var residents = view.Models.Where(model => model.EndpointId == target.Endpoint.Id && model.Instance is not null &&
            model.Yolo == target.Model.Yolo).ToArray();
        if (residents.Length < target.Endpoint.MaxLoadedModels || residents.Any(model => model.Id == target.Model.Id))
            return;
        var idle = residents.FirstOrDefault(model => model.Id != view.Selected && !model.Yolo && !model.Busy &&
            model.Instance is { Busy: false } && model.Model.SupportsUnload);
        if (!target.Endpoint.CanUnload || target.Endpoint.Profile == ProviderProfile.LmStudio || idle is null)
            throw new ProviderFailure(409, "Waiting for an idle provider slot that Mango can release for benchmarking.");
        await providers.ManageAsync(idle.Id, load: false, cancellation, target.Operation);
        await WaitForObservationAsync(cancellation);
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
        if (measurement is not { Before: { Hardware: not null, TotalRam: > 0 } start } window ||
            current.Hardware != start.Hardware || current.TotalRam != start.TotalRam)
            return;
        window.Samples++;
        window.Ram = Math.Max(window.Ram, start.FreeRam - current.FreeRam);
        Dictionary<string, long> resident = [];
        foreach (var device in current.Devices.Where(item => !item.Integrated && item.UsedBytes.HasValue))
            if (start.Devices.FirstOrDefault(item => item.Id == device.Id)?.UsedBytes is { } before)
            {
                resident[device.Id] = Math.Max(0, device.UsedBytes!.Value - before);
                window.Vram[device.Id] = Math.Max(window.Vram.GetValueOrDefault(device.Id), resident[device.Id]);
            }
        window.LastIncrement = new(Math.Max(0, start.FreeRam - current.FreeRam), resident);
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
            return new(workload, pass, null, null, elapsed, null, null, null, null, usage, window.WorkerPeak, window.Samples, SettledIncrement: pass == "cold" ? window.LastIncrement : null);
        }
    }

    private async Task<BenchmarkProfile> SavePassAsync(BenchmarkProfile profile, BenchmarkMeasurement pass,
        bool preserveCompletion = false)
    {
        if (preserveCompletion && profile.Measurements.FirstOrDefault(item => item.Pass == pass.Pass)?.ObservedIncrement is { } known)
            pass = pass with { ObservedIncrement = pass.ObservedIncrement is { } fresh ? Maximum(known, fresh) : known };
        profile = profile with
        {
            Updated = clock.GetUtcNow(), Measurements = profile.Measurements.Where(item => item.Pass != pass.Pass).Append(pass).ToArray(),
            Complete = preserveCompletion && profile.Complete,
            Error = preserveCompletion && (profile.Error != MissingColdAllowance || pass.ObservedIncrement is null) ? profile.Error : null
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
                item.Measurements.Any(pass => pass is null || pass.ObservedIncrement is not null && !ValidCost(pass.ObservedIncrement) ||
                    pass.SettledIncrement is not null && !ValidCost(pass.SettledIncrement) || pass.LoadIdentity is not null && !Hash(pass.LoadIdentity))) ||
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
        ValidCost(item.Load) && ValidCost(item.Execute) &&
        (item.LoadOverride is null || item.LoadOverride.RamBytes is not (< 0 or > (long)LocalSettings.MaximumReserveMiB * MiB) &&
            ValidCost(new(0, item.LoadOverride.DeviceBytes)));
    private static bool ValidCost(MemoryCost? cost) => cost is { RamBytes: >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB,
        DeviceBytes.Count: <= 32 } && cost.DeviceBytes.All(item => item.Key.Length <= 64 &&
            item.Value is >= 0 and <= (long)LocalSettings.MaximumReserveMiB * MiB);
}
