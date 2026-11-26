using System.Net;
using System.Net.Sockets;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ProviderRecoveryChecks
{
    private const string Token = "offline-provider-fixture";

    public static async Task RunAsync(string root)
    {
        await CheckLateStartupAsync(Path.Combine(root, "late-provider"));
        await CheckRefusedRecoveryEndsAsync(Path.Combine(root, "provider-recovery-stop"));
        Console.WriteLine("Offline provider reachability recovery checks passed.");
    }

    private static async Task CheckLateStartupAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var healthy = new Inventory();
        await using var healthyServer = await StartFixtureAsync(0, healthy);
        var latePort = FreePort();
        Guid lateId;
        await using (var setup = new ProviderCatalog(directory))
        {
            await setup.InitializeAsync();
            lateId = (await setup.ConfigureAsync(Registration(Origin(latePort)), default)).Id;
            await setup.ConfigureAsync(Registration(healthyServer.Urls.Single()), default);
        }

        var clock = new MaintenanceClock();
        await using var providers = new ProviderCatalog(directory, time: clock);
        await providers.InitializeAsync();
        using var lifetime = new CancellationTokenSource();
        var worker = providers.RunAsync(lifetime.Token);
        try
        {
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromHours(1)) && clock.HasTimer(TimeSpan.FromSeconds(5)),
                "startup inventory schedules one local reachability retry");
            Check(healthy.Requests == 2 && !providers.View.Endpoints.Single(endpoint => endpoint.Id == lateId).Online,
                "healthy inventory observed at startup and late provider is offline");
            await providers.RefreshAsync(lateId, default);
            await providers.RefreshAsync(lateId, default);
            foreach (var (advance, next) in new[] { (5, 10), (10, 20), (20, 40), (40, 60), (60, 60) })
            {
                clock.Advance(TimeSpan.FromSeconds(advance));
                await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(next)),
                    "refused TCP checks back off to a one-minute cap");
                Check(healthy.Requests == 2, "recovery does not run healthy inventory");
            }

            var late = new Inventory();
            await using var lateServer = await StartFixtureAsync(latePort, late);
            clock.Advance(TimeSpan.FromSeconds(60));
            await UntilAsync(() => providers.View.Models.Any(model => model.EndpointId == lateId && model.Eligible),
                "late provider publishes an eligible loaded model without a manual retry");
            Check(late.Requests == 1 && late.UnexpectedRequests == 0,
                "duplicate signals yield one authenticated inventory refresh and no lifecycle calls");
            var elapsed = clock.GetElapsedTime(0);
            clock.Advance(TimeSpan.FromHours(1) - elapsed - TimeSpan.FromSeconds(1));
            Check(late.Requests == 1 && healthy.Requests == 2, "healthy providers idle until the hourly fallback");
            clock.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => late.Requests == 2 && healthy.Requests == 3, "hourly full inventory still runs");

            var concurrentPort = FreePort();
            var concurrentId = (await providers.ConfigureAsync(Registration(Origin(concurrentPort)), default)).Id;
            const string unsettled = "fixture-prior-deployment";
            providers.RestoreUnsettled(concurrentId, unsettled);
            providers.RequestSettlementRefresh(unsettled);
            providers.RequestSettlementRefresh(unsettled);
            await UntilAsync(() => clock.HasTimer(ProviderLimits.RefreshCoalesceInterval) && clock.HasTimer(TimeSpan.FromSeconds(5)),
                "triggered inventory and TCP recovery are both queued");
            var concurrent = new Inventory();
            await using var concurrentServer = await StartFixtureAsync(concurrentPort, concurrent);
            clock.Advance(TimeSpan.FromSeconds(5));
            await UntilAsync(() => providers.View.Endpoints.Single(endpoint => endpoint.Id == concurrentId).Online &&
                clock.HasTimer(TimeSpan.FromHours(1)), "coalesced maintenance completes");
            Check(concurrent.Requests == 1 && concurrent.UnexpectedRequests == 0,
                "a recovery and queued provider event share one authenticated inventory refresh");
        }
        finally
        {
            await lifetime.CancelAsync();
            await worker;
        }
    }

    private static async Task CheckRefusedRecoveryEndsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var refused = new Inventory(rejectCredential: true);
        var refusedPort = FreePort();
        var disabledPort = FreePort(refusedPort);
        var stoppingPort = FreePort(refusedPort, disabledPort);
        var clock = new MaintenanceClock();
        await using var providers = new ProviderCatalog(directory, time: clock);
        await providers.InitializeAsync();
        var refusedId = (await providers.ConfigureAsync(Registration(Origin(refusedPort)), default)).Id;
        var disabledId = (await providers.ConfigureAsync(Registration(Origin(disabledPort)), default)).Id;
        await providers.ConfigureAsync(Registration(Origin(stoppingPort)), default);
        var disabled = new Inventory();
        var stopped = new Inventory();
        using var lifetime = new CancellationTokenSource();
        var worker = providers.RunAsync(lifetime.Token);
        try
        {
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(5)) && clock.HasTimer(TimeSpan.FromHours(1)),
                "worker wakes only for offline endpoints");
            await providers.RemoveAsync(disabledId, default);
            await using var refusedServer = await StartFixtureAsync(refusedPort, refused);
            await using var disabledServer = await StartFixtureAsync(disabledPort, disabled);
            clock.Advance(TimeSpan.FromSeconds(5));
            await UntilAsync(() => refused.Requests == 1 && clock.HasTimer(TimeSpan.FromSeconds(10)),
                "a reachable port gets one inventory check and the remaining offline endpoint reschedules");
            Check(!providers.View.Endpoints.Single(endpoint => endpoint.Id == refusedId).Online,
                "rejecting the unchanged credential keeps the provider offline");
            clock.Advance(TimeSpan.FromSeconds(10));
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(20)), "offline endpoint continues backing off");
            Check(disabled.Requests == 0 && refused.Requests == 1,
                "disable removes pending recovery and rejected credentials do not start fast checks");
        }
        finally
        {
            await lifetime.CancelAsync();
            await worker;
        }
        await using var stoppedServer = await StartFixtureAsync(stoppingPort, stopped);
        clock.Advance(TimeSpan.FromHours(2));
        Check(stopped.Requests == 0 && !clock.HasTimers, "shutdown cancels queued recovery and inventory waits");
    }

    private static string Origin(int port) => $"http://127.0.0.1:{port}";

    private static EndpointUpdate Registration(string address) =>
        new(null, ProviderProfile.Generic, ProviderMode.Attached, address, Token, false, null, null);

    private static int FreePort(params int[] used)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!used.Contains(port))
                return port;
        }
    }

    private static async Task<WebApplication> StartFixtureAsync(int port, Inventory inventory)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Origin(port));
        var app = builder.Build();
        app.MapGet("/api/v1/models", inventory.Read);
        app.MapFallback(() => inventory.Unexpected());
        await app.StartAsync();
        return app;
    }

    private sealed class Inventory(bool rejectCredential = false)
    {
        private int requests;
        private int unexpected;
        public int Requests => Volatile.Read(ref requests);
        public int UnexpectedRequests => Volatile.Read(ref unexpected);

        public IResult Read(HttpRequest request)
        {
            Interlocked.Increment(ref requests);
            if (request.Headers.Authorization != $"Bearer {Token}")
                return Unexpected();
            if (rejectCredential)
                return Results.Unauthorized();
            return Results.Text("""
                {"models":[{"key":"fixture","type":"llm","loaded_instances":[
                    {"id":"fixture","config":{"context_length":4096}}
                ]}]}
                """, "application/json");
        }

        public IResult Unexpected()
        {
            Interlocked.Increment(ref unexpected);
            return Results.StatusCode(403);
        }
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException($"Provider recovery check failed: {description}");
    }

    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition() && !limit.IsCancellationRequested)
            await Task.Delay(10);
        Check(condition(), description);
    }

    private sealed class MaintenanceClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<OneShot> timers = [];
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public bool HasTimers { get { lock (gate) return timers.Count != 0; } }

        public bool HasTimer(TimeSpan delay)
        {
            lock (gate) return timers.Any(timer => timer.Due - ticks == delay.Ticks);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new OneShot(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            OneShot[] ready;
            lock (gate)
            {
                ticks += elapsed.Ticks;
                ready = timers.Where(timer => timer.Due <= ticks).ToArray();
                timers.RemoveAll(timer => timer.Due <= ticks);
            }
            foreach (var timer in ready)
                timer.Fire();
        }

        private sealed class OneShot(MaintenanceClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == Timeout.InfiniteTimeSpan, "fixture timers are one-shot");
                lock (clock.gate)
                {
                    clock.timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = clock.ticks + dueTime.Ticks;
                        clock.timers.Add(this);
                    }
                }
                return true;
            }

            public void Fire() => callback(state);
            public void Dispose() { lock (clock.gate) clock.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
