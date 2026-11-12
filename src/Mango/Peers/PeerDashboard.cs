using System.Text.Json;
using System.Threading.Channels;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    private static readonly JsonSerializerOptions DashboardJson = new(Json) { WriteIndented = false };
    private readonly SemaphoreSlim dashboardReaders = new(8);
    private event Action<int>? DashboardChanged;

    private void SignalNetwork() => DashboardChanged?.Invoke(1);
    private void SignalRequests() => DashboardChanged?.Invoke(2);

    public async Task WatchDashboardAsync(HttpContext context, CancellationToken cancellation)
    {
        if (!dashboardReaders.Wait(0))
            throw new PeerFailure(429, "Too many local dashboards are open.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
            context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
        });
        var flags = 2;
        void Signal(int change)
        {
            Interlocked.Or(ref flags, change);
            changes.Writer.TryWrite(true);
        }
        DashboardChanged += Signal;
        changes.Writer.TryWrite(true);
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            while (await changes.Reader.WaitToReadAsync(stop.Token))
            {
                changes.Reader.TryRead(out _);
                var changed = Interlocked.Exchange(ref flags, 0);
                if (changed != 0)
                {
                    var frame = JsonSerializer.Serialize(new
                    {
                        Network = (changed & 1) != 0, Requests = (changed & 2) != 0 ? SchedulingView() : null
                    }, DashboardJson);
                    await context.Response.WriteAsync(frame + "\n", stop.Token);
                    await context.Response.Body.FlushAsync(stop.Token);
                }
                await Task.Delay(100, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            DashboardChanged -= Signal;
            dashboardReaders.Release();
        }
    }

    private void RecordContact(Guid node, bool reachable, string? digest)
    {
        var at = clock.GetUtcNow();
        var old = contacts.GetValueOrDefault(node);
        contacts[node] = reachable ? new(at, true, digest, at) : new(old?.LastContact, false, old?.Digest, at);
        if (old?.Reachable != reachable || reachable && old?.Digest != digest)
            SignalNetwork();
    }
}
