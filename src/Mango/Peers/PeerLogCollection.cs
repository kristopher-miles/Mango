using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Mango.Logging;
using Mango.Providers;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    private readonly object logCollectionSync = new();
    private readonly SemaphoreSlim logTransferSlots = new(2);
    private CollectionJob? logJob;
    private string? logJobError;
    private sealed record CollectionJob(DateOnly Day, Task<LogCollection?> Work);
    private sealed record CollectDay(DateOnly Day);

    public void MapLocalLogs(RouteGroupBuilder local)
    {
        var routes = local.MapGroup("/logs");
        routes.MapGet("", (DateOnly? day, string? key, string? source, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => ReadLogViewAsync(context.Request, day, key, source, token)));
        routes.MapPut("/settings", (LogSettingsUpdate update, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => SetLogPolicyAsync(context.Request, update, token)));
        routes.MapPost("/clear", (LogClearRequest input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => ClearLogsAsync(context.Request, input.Confirm, token)));
        routes.MapPost("/collect", (CollectDay input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, _ =>
            {
                LogWindowFor(input.Day, Logs.Configuration.Policy.TimeZone);
                var job = StartLogCollection(input.Day, manual: true);
                context.Response.StatusCode = 202;
                return Task.FromResult(new { job.Day, Running = !job.Work.IsCompleted });
            }));
        routes.MapGet("/export", (DateOnly day, HttpContext context) => ExportLogsAsync(day, context));
    }

    public void MapPeerLogs(RouteGroupBuilder peer)
    {
        var routes = peer.MapGroup("/logs");
        routes.MapPost("/page", (LogTransfer input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => TransferPageAsync(input, token)));
        routes.MapPost("/payload", (LogPayloadTransfer input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => TransferPayloadAsync(input, token)));
        routes.MapPost("/coverage", (LogTransfer input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token =>
            {
                VerifyLogTransfer(input.Epoch, input.Day, input.TimeZone);
                return Logs.ReadCollectionAsync(input.Epoch, input.Day, input.TimeZone, token);
            }));
        routes.MapPost("/collect", (LogTransfer input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, async token =>
            {
                _ = VerifyLogTransfer(input.Epoch, input.Day, input.TimeZone);
                var job = StartLogCollection(input.Day, manual: true);
                if (job.Day != input.Day)
                    return new LogCollection(input.Epoch, input.Day, input.TimeZone, Snapshot.Self?.Id ?? Guid.Empty, clock.GetUtcNow(), []);
                return await job.Work.WaitAsync(token) ??
                    new LogCollection(input.Epoch, input.Day, input.TimeZone, Snapshot.Self?.Id ?? Guid.Empty, clock.GetUtcNow(), []);
            }));
    }

    private async Task<LogPage> TransferPageAsync(LogTransfer input, CancellationToken cancellation)
    {
        if (input.After < 0 || input.Node == Guid.Empty)
            throw new PeerFailure(400, "The log watermark is invalid.");
        var window = VerifyLogTransfer(input.Epoch, input.Day, input.TimeZone);
        if (!logTransferSlots.Wait(0))
            return new(LogCoverage.Error, [], input.After, false);
        try { return await Logs.ReadPageAsync(input.Epoch, window, input.Node, input.After, cancellation); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(LogCoverage.Error, [], input.After, false);
        }
        finally { logTransferSlots.Release(); }
    }

    private async Task<LogChunk?> TransferPayloadAsync(LogPayloadTransfer input, CancellationToken cancellation)
    {
        if (input.Event == Guid.Empty || input.Node == Guid.Empty || input.Sequence <= 0 || input.Offset < 0)
            throw new PeerFailure(400, "The requested log payload is invalid.");
        var window = VerifyLogTransfer(input.Epoch, input.Day, input.TimeZone);
        if (!logTransferSlots.Wait(0))
            return null;
        try { return await Logs.ReadChunkAsync(input, window, cancellation); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        finally { logTransferSlots.Release(); }
    }

    private LogWindow VerifyLogTransfer(LogEpoch epoch, DateOnly day, string zone)
    {
        var config = Logs.Configuration;
        if (!Ready || !IsMember || !Logs.Available(epoch) || zone != config.Policy.TimeZone)
            throw new PeerFailure(409, "Synchronize the shared log configuration before collecting.");
        return LogWindowFor(day, zone);
    }

    private static LogWindow LogWindowFor(DateOnly day, string zone)
    {
        if (day.Year is < 2000 or > 9997 || LogPolicy.Zone(zone) is not { } timezone)
            throw new PeerFailure(400, "Select a valid day in the configured timezone.");
        return LogWindow.For(day, timezone);
    }

    private DateOnly Today(string zone) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(clock.GetUtcNow(), LogPolicy.Zone(zone)!).DateTime);

    private CollectionJob StartLogCollection(DateOnly day, bool manual)
    {
        lock (logCollectionSync)
        {
            if (logJob is { Work.IsCompleted: false } running)
                return running;
            Volatile.Write(ref logJobError, null);
            var job = Task.Run(() => CollectLogsAsync(day, manual, stopping.Token));
            return logJob = new(day, job);
        }
    }

    private async Task RunLogDaysAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var zone = Logs.Configuration.Policy.TimeZone;
                var today = Today(zone);
                var next = LogWindowFor(today, zone).End;
                var wait = next - clock.GetUtcNow();
                if (wait > TimeSpan.Zero && await logSchedule.WaitAsync(wait, cancellation))
                    continue;
                await Logs.PrepareAsync(cancellation);
                if (Logs.Configuration.Policy.TimeZone == zone && Today(zone) > today)
                    _ = StartLogCollection(today, manual: false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task StopLogCollectionAsync()
    {
        CollectionJob? job;
        lock (logCollectionSync)
            job = logJob;
        if (job is not null)
            await job.Work;
        logTransferSlots.Dispose();
        logSchedule.Dispose();
    }

    private async Task<LogCollection?> CollectLogsAsync(DateOnly day, bool manual, CancellationToken cancellation)
    {
        var config = Logs.Configuration;
        var snapshot = Snapshot;
        var window = LogWindowFor(day, config.Policy.TimeZone);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        try
        {
            await Logs.PrepareAsync(token);
            if (!Logs.Available(config.Epoch))
                return null;
            var previous = await Logs.ReadCollectionAsync(config.Epoch, day, config.Policy.TimeZone, token);
            var all = snapshot.Nodes.Values.Where(node => !node.Removed).OrderBy(node => node.Joined).ThenBy(node => node.Id).ToArray();
            if (snapshot.Self is { } self && Ready)
                foreach (var lower in all.TakeWhile(node => node.Id != self.Id))
                {
                    if (await SendAsync<object>(snapshot, lower, "ping", null, token, refresh: true) is not { Authenticated: true })
                        continue;
                    if (!manual)
                        return previous;
                    var request = new LogTransfer(config.Epoch, day, config.Policy.TimeZone, lower.Id, 0);
                    var remote = await SendAsync<LogCollection>(snapshot, lower, "logs/collect", request, token,
                        timeout: TimeSpan.FromMinutes(2));
                    if (remote?.Body is { } collection && collection.Epoch == config.Epoch && collection.Day == day &&
                        collection.TimeZone == config.Policy.TimeZone && collection.Sources is { Length: <= PeerLimits.Nodes })
                        return await CopyCollectionAsync(config, window, snapshot, lower, previous, collection, token);
                    Volatile.Write(ref logJobError, "The elected collector is reachable. Its current aggregate could not be retrieved.");
                    return previous;
                }

            var sources = all.Select(node => (node.Id, node.Name, Peer: node.Id == config.Node ? null : node)).ToList();
            if (sources.All(item => item.Id != config.Node))
                sources.Insert(0, (config.Node, config.Name ?? "This host", null));
            var coverage = sources.Select(item => new LogSource(item.Id, item.Name, LogCoverage.Unavailable,
                PreviousWatermark(previous, item.Id), clock.GetUtcNow())).ToArray();
            var result = new LogCollection(config.Epoch, day, config.Policy.TimeZone, config.Node, clock.GetUtcNow(), coverage);
            await Logs.SaveCollectionAsync(result, token);
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                coverage[i] = await CollectSourceAsync(config, window, snapshot, source.Peer, coverage[i], token);
                await Logs.SaveCollectionAsync(result, token);
            }
            if (coverage.Any(item => item.Coverage is LogCoverage.Unavailable or LogCoverage.Error))
                await MergeCachedCollectionsAsync(config, window, snapshot, result, token);
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!cancellation.IsCancellationRequested)
                Volatile.Write(ref logJobError, "The collection time limit was reached. Available records remain saved.");
        }
        catch (Exception error) when (error is PeerFailure or IOException or UnauthorizedAccessException or JsonException)
        {
            Volatile.Write(ref logJobError, "Mango could not finish this collection. Available records remain saved.");
        }
        return null;
    }

    private async Task MergeCachedCollectionsAsync(LogConfiguration config, LogWindow window, PeerSnapshot snapshot,
        LogCollection local, CancellationToken cancellation)
    {
        var cached = new List<(PeerNode Peer, LogCollection Collection)>();
        foreach (var source in local.Sources.Where(item => item.Coverage is LogCoverage.Received or LogCoverage.Off))
        {
            if (source.Node == config.Node || !snapshot.Nodes.TryGetValue(source.Node, out var peer))
                continue;
            var request = new LogTransfer(config.Epoch, window.Day, config.Policy.TimeZone, peer.Id, 0);
            var reply = await SendAsync<LogCollection>(snapshot, peer, "logs/coverage", request, cancellation);
            if (reply?.Body is { } collection && collection.Epoch == config.Epoch && collection.Day == window.Day &&
                collection.TimeZone == config.Policy.TimeZone && collection.Sources is { Length: <= PeerLimits.Nodes })
                cached.Add((peer, collection));
        }
        for (var i = 0; i < local.Sources.Length; i++)
        {
            var unavailable = local.Sources[i];
            if (unavailable.Coverage is not (LogCoverage.Unavailable or LogCoverage.Error))
                continue;
            var candidate = cached.Select(item => (item.Peer, Source: item.Collection.Sources.FirstOrDefault(source =>
                    source?.Node == unavailable.Node && source.Watermark > 0)))
                .Where(item => item.Source is not null).OrderByDescending(item => item.Source!.Watermark).FirstOrDefault();
            if (candidate.Source is null)
                continue;
            var fetched = await CollectSourceAsync(config, window, snapshot, candidate.Peer, unavailable with { Watermark = 0 }, cancellation);
            if (fetched.Watermark > 0)
            {
                local.Sources[i] = unavailable with { FromCache = true, Shortened = fetched.Shortened, Dropped = fetched.Dropped };
                await Logs.SaveCollectionAsync(local, cancellation);
            }
        }
    }

    private async Task<LogCollection> CopyCollectionAsync(LogConfiguration config, LogWindow window, PeerSnapshot snapshot,
        PeerNode collector, LogCollection? previous, LogCollection remote, CancellationToken cancellation)
    {
        var nodes = snapshot.Nodes;
        var sources = remote.Sources.Where(source => source is not null && nodes.ContainsKey(source.Node))
            .DistinctBy(source => source.Node).Select(source => source with
            {
                Name = nodes[source.Node].Name, Watermark = PreviousWatermark(previous, source.Node)
            }).ToArray();
        var copy = remote with { Sources = sources };
        await Logs.SaveCollectionAsync(copy, cancellation);
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            if (source.Node == config.Node)
                sources[i] = await CollectSourceAsync(config, window, snapshot, null, source, cancellation);
            else if (source.Coverage is LogCoverage.Received or LogCoverage.Off || remote.Sources.Any(item => item?.Node == source.Node && item.Watermark > 0))
            {
                var fetched = await CollectSourceAsync(config, window, snapshot, collector, source, cancellation);
                sources[i] = fetched with { Coverage = fetched.Coverage == LogCoverage.Received ? source.Coverage : fetched.Coverage };
            }
            await Logs.SaveCollectionAsync(copy, cancellation);
        }
        return copy;
    }

    private async Task<LogSource> CollectSourceAsync(LogConfiguration config, LogWindow window, PeerSnapshot snapshot,
        PeerNode? peer, LogSource source, CancellationToken cancellation)
    {
        var after = source.Watermark;
        var coverage = LogCoverage.Received;
        var limited = source.Shortened;
        var dropped = source.Dropped;
        try
        {
            while (Logs.Available(config.Epoch))
            {
                cancellation.ThrowIfCancellationRequested();
                var request = new LogTransfer(config.Epoch, window.Day, config.Policy.TimeZone, source.Node, after);
                var page = peer is null ? await Logs.ReadPageAsync(config.Epoch, window, source.Node, after, cancellation, rawOnly: true) :
                    (await SendAsync<LogPage>(snapshot, peer, "logs/page", request, cancellation, refresh: true))?.Body;
                if (page is null)
                {
                    coverage = LogCoverage.Unavailable;
                    break;
                }
                if (!ValidPage(page, source.Node, window, after))
                {
                    coverage = LogCoverage.Error;
                    break;
                }
                coverage = page.Coverage;
                limited |= page.Shortened;
                dropped |= page.Dropped;
                if (peer is not null)
                {
                    foreach (var entry in page.Entries)
                    {
                        await CopyPayloadAsync(config, window, snapshot, peer, entry, response: false, cancellation);
                        await CopyPayloadAsync(config, window, snapshot, peer, entry, response: true, cancellation);
                    }
                    await Logs.ImportAsync(config.Epoch, source.Node, page.Entries, cancellation);
                }
                else
                    await Logs.ImportAsync(config.Epoch, source.Node, page.Entries, cancellation, metadataOnly: true);
                after = page.Watermark;
                if (!page.More)
                    break;
                await Task.Delay(25, cancellation);
            }
        }
        catch (Exception error) when (error is PeerFailure or IOException or UnauthorizedAccessException or JsonException)
        {
            coverage = LogCoverage.Error;
        }
        return source with { Coverage = coverage, Watermark = after, ObservedAt = clock.GetUtcNow(), Shortened = limited, Dropped = dropped };
    }

    private static bool ValidPage(LogPage page, Guid node, LogWindow window, long after)
    {
        if (!Enum.IsDefined(page.Coverage) || page.Entries is not { Length: <= LogStore.PageSize })
            return false;
        foreach (var entry in page.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || entry.Node != node || !Enum.IsDefined(entry.Kind) ||
                entry.At < window.Start || entry.At >= window.End || entry.Sequence <= after ||
                entry.Routes is { Length: > MaxDestinations } || entry.Reason is { Length: > 600 } ||
                entry.Input?.Stored is < 0 or > ProviderLimits.MaxRequestBytes ||
                entry.Output?.Stored is < 0 or > ProviderLimits.MaxRequestBytes)
                return false;
            after = entry.Sequence;
        }
        return after == page.Watermark && (!page.More || page.Entries.Length > 0);
    }

    private async Task CopyPayloadAsync(LogConfiguration config, LogWindow window, PeerSnapshot snapshot, PeerNode peer,
        LogEntry entry, bool response, CancellationToken cancellation)
    {
        var payload = response ? entry.Output : entry.Input;
        if (payload is not { Stored: > 0 } || entry.At < clock.GetUtcNow().AddDays(-config.Policy.RawDays))
            return;
        long offset = 0;
        while (offset < payload.Stored && Logs.Available(config.Epoch))
        {
            var request = new LogPayloadTransfer(config.Epoch, window.Day, config.Policy.TimeZone, entry.Node, entry.Id,
                entry.At, entry.Sequence, response, offset);
            var reply = (await SendAsync<LogChunk>(snapshot, peer, "logs/payload", request, cancellation))?.Body;
            if (reply is not { Data.Length: > 0 and <= LogStore.ChunkBytes } || reply.Length > payload.Stored ||
                offset + reply.Data.Length > reply.Length ||
                !await Logs.StageChunkAsync(config.Epoch, entry.Id, response, offset, reply.Data, cancellation))
                return;
            offset += reply.Data.Length;
            await Task.Delay(25, cancellation);
        }
    }

    private static long PreviousWatermark(LogCollection? previous, Guid node) =>
        previous?.Sources.FirstOrDefault(source => source.Node == node)?.Watermark ?? 0;

    private sealed record LogClearRequest(bool Confirm);
}
