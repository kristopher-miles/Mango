using System.ComponentModel;
using Mango.Host;
using Mango.Providers;
using Mango.Resources;

internal static class TelemetryPolicyChecks
{
    public static async Task RunAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "telemetry-policy-edges")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        var clock = new Clock();
        using var resources = new ResourceProtection(directory, settings, (_, _) => throw new Win32Exception(),
            () => throw new Win32Exception(), clock);
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var failures = new List<string>();
        if (!resources.ServingAllowed) failures.Add("No-hold Contributor waits for a first observation.");
        await resources.SampleAndApplyAsync(() => providers.View, default);
        if (!resources.ServingAllowed) failures.Add("First missing report still leaves startup contribution blocked.");
        ResourceObservation Sample(double cpu) => new(clock.GetUtcNow(), 16L * 1024 * 1024 * 1024,
            12L * 1024 * 1024 * 1024, cpu, 0, [], "fixture", [], true, null);
        resources.ApplyObservation(Sample(0));
        resources.ApplyObservation(Sample(100));
        for (var i = 0; i < 5; i++) { clock.Advance(2); resources.ApplyObservation(Sample(100)); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            await resources.AdvanceWithdrawalAsync(providers, deadline.Token);
            if (resources.Status.State != ContributionState.Withdrawing) break;
            await Task.Delay(10, deadline.Token);
        } while (!deadline.IsCancellationRequested);
        if (resources.Status.State != ContributionState.Cooldown) failures.Add("Measured sustained external activity did not enter cooldown.");
        resources.ApplyObservation(Sample(0));
        clock.Advance(301);
        for (var i = 0; i < 4; i++) await resources.SampleAndApplyAsync(() => providers.View, default);
        if (!resources.ServingAllowed) failures.Add("Sampling exceptions keep an expired user cooldown blocked.");
        var heldDirectory = Directory.CreateDirectory(Path.Combine(root, "telemetry-hard-hold")).FullName;
        await new SnapshotFile<ResourceData>(Path.Combine(heldDirectory, "resources.json"), _ => null)
            .LoadAsync(ResourceData.Empty with { Hold = new(ContributionState.Fault, "Synthetic native release failure") });
        using (var held = new ResourceProtection(heldDirectory, settings, (_, _) => throw new Win32Exception(),
            () => throw new Win32Exception(), clock))
        {
            await held.InitializeAsync();
            await using var heldProviders = new ProviderCatalog(heldDirectory, held);
            await heldProviders.InitializeAsync();
            await held.InspectStartupAsync(heldProviders, default);
            if (held.Status.State != ContributionState.Fault || held.ServingAllowed)
                failures.Add("Unavailable observation cleared a persisted hard hold.");
        }
        foreach (var hasPending in new[] { false, true })
        {
            var oldDirectory = Directory.CreateDirectory(Path.Combine(root, "legacy-pressure-" + hasPending)).FullName;
            PendingRelease[] pending = hasPending ? [new(Guid.NewGuid(), new string('a', 64), [], Loading: true)] : [];
            await new SnapshotFile<ResourceData>(Path.Combine(oldDirectory, "resources.json"), _ => null)
                .LoadAsync(ResourceData.Empty with { Hold = new(ContributionState.Fault, "Dedicated memory pressure on fixture.", pending) });
            using var migrated = new ResourceProtection(oldDirectory, settings, (_, _) => Sample(0), clock: clock);
            await migrated.InitializeAsync();
            if (hasPending ? migrated.Status.State != ContributionState.Withdrawing : !migrated.ServingAllowed)
                failures.Add("Legacy pressure migration lost the distinction between a memory hold and unfinished work.");
            var disk = new SnapshotFile<ResourceData>(Path.Combine(oldDirectory, "resources.json"), _ => null);
            await disk.LoadAsync(ResourceData.Empty);
            if (hasPending ? disk.Value.Hold?.Pending?.Single().Loading != true : disk.Value.Hold is not null)
                failures.Add("Legacy pressure migration did not persist the expected release fence.");
        }
        var manualDirectory = Directory.CreateDirectory(Path.Combine(root, "telemetry-manual-hold")).FullName;
        var manualSettings = new SnapshotFile<LocalSettings>(Path.Combine(manualDirectory, "local-settings.json"), LocalSettings.Validate);
        await manualSettings.LoadAsync(new LocalSettings { Role = LocalRole.Leech });
        using (var manual = new ResourceProtection(manualDirectory, manualSettings, (_, _) => throw new Win32Exception(),
            () => throw new Win32Exception(), clock))
        {
            await manual.InitializeAsync();
            await manual.SampleAndApplyAsync(() => providers.View, default);
            if (manual.ServingAllowed || manual.Status.State == ContributionState.Contributor)
                failures.Add("Unavailable observation cleared an operator leech hold.");
        }
        foreach (var scenario in new[] { "cpu-only", "gpu-only", "unattributed" })
        {
            var partialDirectory = Directory.CreateDirectory(Path.Combine(root, "partial-activity-" + scenario)).FullName;
            using var partial = new ResourceProtection(partialDirectory, settings, (_, _) => Sample(0),
                () => ((ulong)(16L * 1024 * 1024 * 1024), (ulong)(12L * 1024 * 1024 * 1024)), clock);
            await partial.InitializeAsync();
            partial.ApplyObservation(Sample(0));
            for (var i = 0; i < 6; i++)
            {
                partial.ApplyObservation(Sample(100) with {
                    ExternalCpu = scenario == "gpu-only" ? null : 100,
                    ExternalGpu = scenario == "cpu-only" ? null : 100,
                    WorkerAttribution = scenario != "unattributed" });
                clock.Advance(2);
            }
            if (scenario == "unattributed" ? partial.Status.State != ContributionState.Contributor
                : partial.Status.State != ContributionState.Withdrawing)
                failures.Add("Partial measured external activity policy failed: " + scenario);
        }
        foreach (var failure in failures) Console.WriteLine("POLICY GAP: " + failure);
        if (failures.Count != 0) throw new InvalidOperationException("Telemetry policy edge regressions failed: " + failures.Count);
        Console.WriteLine("Telemetry startup, exception cooldown and hard/operator hold edge checks passed.");
    }
    public static async Task CheckNativeStopCrosscheckAsync()
    {
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec")!, "/c exit 0")
            { UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync();
        var endpoint = Guid.NewGuid();
        ResourceObservation Old(int[] ids) => new(DateTimeOffset.UtcNow.AddMinutes(-1), 0, 0, null, null, [], null,
            ids.Length == 0 ? [] : new() { [endpoint] = new(ids, 0, null, null) }, false, "stale fixture sample");
        var verify = typeof(ResourceProtection).GetMethod("VerifyRelease",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string? Verify(int stopped, int[] prior, int[] after) => (string?)verify.Invoke(null,
            [new ReleaseResult([new ReleasedEndpoint(endpoint, stopped)]), Old(prior), Old(after)]);
        if (Verify(Environment.ProcessId, [], []) is null ||
            Verify(child.Id, [Environment.ProcessId], []) is null ||
            Verify(child.Id, [child.Id], [Environment.ProcessId]) is null)
            throw new InvalidOperationException("A live root or tracked worker bypassed native release protection.");
        Console.WriteLine("Live stopped PID, before-worker and after-worker native protection checks passed.");
        var error = Verify(child.Id, [child.Id], [child.Id]);
        if (error is not null)
            throw new InvalidOperationException("Authoritative completed native stop is blocked by stale optional worker observation: " + error);
        Console.WriteLine("Authoritative native stop with stale optional crosscheck passed.");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
