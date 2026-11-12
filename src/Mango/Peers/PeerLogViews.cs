using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Mango.Logging;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    private readonly SemaphoreSlim logExports = new(1);
    private static readonly JsonSerializerOptions ExportJson = new(Json) { WriteIndented = false };

    private async Task<object> ReadLogViewAsync(HttpRequest request, DateOnly? requested, string? key, string? source,
        CancellationToken cancellation)
    {
        if (key?.Length > 120 || source?.Length > 64)
            throw new PeerFailure(400, "The log filter is too long.");
        var config = Logs.Configuration;
        var day = requested ?? Today(config.Policy.TimeZone);
        var window = LogWindowFor(day, config.Policy.TimeZone);
        var snapshot = Snapshot;
        CollectionJob? job;
        lock (logCollectionSync)
            job = logJob;
        var running = job is { Work.IsCompleted: false };
        LogCollection? collected = null;
        LogSource[] coverage = [];
        List<LogEntry> details = [];
        var keys = new Dictionary<string, Usage>(StringComparer.Ordinal);
        var sources = new Dictionary<string, Usage>(StringComparer.Ordinal);
        var scanned = 0;
        var requests = 0;
        var attempts = 0;
        var capped = false;
        if (Logs.Available(config.Epoch))
        {
            collected = await Logs.ReadCollectionAsync(config.Epoch, day, config.Policy.TimeZone, cancellation);
            coverage = await CoverageAsync(config, window, snapshot, collected, cancellation);
            await foreach (var entry in LogEntriesAsync(config, window, cancellation))
            {
                if (++scanned > 10_000)
                {
                    capped = true;
                    break;
                }
                if (key is { Length: > 0 } && entry.KeyName?.Contains(key, StringComparison.OrdinalIgnoreCase) != true ||
                    source is { Length: > 0 } && entry.Source?.Address?.Contains(source, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                if (entry.Kind == LogKind.Request)
                {
                    requests++;
                    AddUsage(keys, entry.KeyName ?? "Local session", entry);
                    AddUsage(sources, entry.Source?.Address ?? "Unknown", entry);
                }
                if (entry.Kind == LogKind.Attempt)
                    attempts++;
                details.Add(entry);
                if (details.Count == 160)
                    details = details.OrderByDescending(item => item.At).Take(80).ToList();
            }
        }
        var result = new
        {
            Settings = config.Policy, Day = day, Today = Today(config.Policy.TimeZone), window.Start, window.End,
            CanManage = snapshot.Records.Identity is null || HasAdmin(snapshot, AdminHeader(request)),
            Collection = new { CollectedAt = collected?.CollectedAt, Collector = collected?.Collector,
                Running = running, RunningDay = running ? job?.Day : null,
                Error = Volatile.Read(ref logJobError), Sources = coverage },
            Store = Logs.Status, Clear = LogClearView(snapshot),
            Requests = requests, Attempts = attempts, Capped = capped,
            ByKey = keys.Values.OrderByDescending(item => item.Requests).ToArray(),
            BySource = sources.Values.OrderByDescending(item => item.Requests).ToArray(),
            Entries = details.OrderByDescending(item => item.At).Take(80).ToArray()
        };
        if (Logs.Configuration.Epoch != config.Epoch)
            throw new PeerFailure(409, "Log history was cleared while reading. Refresh the view.");
        return result;
    }

    private sealed class Usage(string name)
    {
        public string Name { get; } = name;
        public int Requests { get; set; }
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
    }

    private static void AddUsage(Dictionary<string, Usage> values, string name, LogEntry entry)
    {
        if (!values.TryGetValue(name, out var row))
        {
            var label = values.Count < 512 ? name : "Other";
            if (!values.TryGetValue(label, out row))
                values[label] = row = new(label);
        }
        row.Requests++;
        row.BytesIn += entry.RequestBytes ?? 0;
        row.BytesOut += entry.ResponseBytes ?? 0;
    }

    private async Task<LogSource[]> CoverageAsync(LogConfiguration config, LogWindow window, PeerSnapshot snapshot,
        LogCollection? collected, CancellationToken cancellation)
    {
        var now = clock.GetUtcNow();
        var local = await Logs.ReadPageAsync(config.Epoch, window, config.Node, 0, cancellation);
        var sources = snapshot.Others.Select(peer => collected?.Sources.FirstOrDefault(source => source.Node == peer.Id) ??
            new LogSource(peer.Id, peer.Name, LogCoverage.Unavailable, 0, now)).ToList();
        sources.Insert(0, new(config.Node, config.Name ?? "This host", local.Coverage, local.Watermark, now, local.Shortened, local.Dropped));
        if (config.Node != Guid.Empty)
        {
            var prior = await Logs.ReadPageAsync(config.Epoch, window, Guid.Empty, 0, cancellation);
            if (prior.Entries.Length != 0)
                sources.Add(new(Guid.Empty, "Standalone history on this host", prior.Coverage, prior.Watermark, now, prior.Shortened, prior.Dropped));
        }
        return sources.ToArray();
    }

    private async IAsyncEnumerable<LogEntry> LogEntriesAsync(LogConfiguration config, LogWindow window,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        foreach (var node in Logs.AggregateNodes(window).Prepend(Guid.Empty).Prepend(config.Node).Distinct())
        {
            long after = 0;
            while (true)
            {
                var page = await Logs.ReadPageAsync(config.Epoch, window, node, after, cancellation);
                foreach (var entry in page.Entries)
                    yield return entry;
                if (!page.More)
                    break;
                after = page.Watermark;
                await Task.Yield();
            }
        }
    }

    private async Task ExportLogsAsync(DateOnly day, HttpContext context)
    {
        if (!logExports.Wait(0))
        {
            context.Response.StatusCode = 429;
            return;
        }
        var cancellation = context.RequestAborted;
        try
        {
            var config = Logs.Configuration;
            var window = LogWindowFor(day, config.Policy.TimeZone);
            bool collecting;
            lock (logCollectionSync)
                collecting = logJob is { Work.IsCompleted: false } job && job.Day == day;
            var collected = await Logs.ReadCollectionAsync(config.Epoch, day, config.Policy.TimeZone, cancellation);
            var coverage = await CoverageAsync(config, window, Snapshot, collected, cancellation);
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"mango-logs-{day:yyyy-MM-dd}.zip\"";
            context.Response.Headers.CacheControl = "no-store";
            // ZIP finalizes compressed entries synchronously even when their writes are asynchronous.
            if (context.Features.Get<IHttpBodyControlFeature>() is { } body)
                body.AllowSynchronousIO = true;
            await using var zip = await ZipArchive.CreateAsync(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true,
                entryNameEncoding: Encoding.UTF8, cancellationToken: cancellation);
            var records = 0;
            var incomplete = 0;
            await using (var stream = await zip.CreateEntry("records.ndjson", CompressionLevel.Fastest).OpenAsync(cancellation))
                await foreach (var entry in LogEntriesAsync(config, window, cancellation))
                {
                    await JsonSerializer.SerializeAsync(stream, entry, ExportJson, cancellation);
                    await stream.WriteAsync("\n"u8.ToArray(), cancellation);
                    records++;
                }
            await foreach (var entry in LogEntriesAsync(config, window, cancellation))
            {
                if (entry.Input is { } input &&
                    !await WriteZipPayloadAsync(zip, config, window, entry, input, response: false, cancellation))
                    incomplete++;
                if (entry.Output is { } output &&
                    !await WriteZipPayloadAsync(zip, config, window, entry, output, response: true, cancellation))
                    incomplete++;
            }
            await using var manifest = await zip.CreateEntry("manifest.json", CompressionLevel.Fastest).OpenAsync(cancellation);
            await JsonSerializer.SerializeAsync(manifest, new
            {
                Day = day, TimeZone = config.Policy.TimeZone, window.Start, window.End, Coverage = coverage,
                Complete = !collecting && coverage.All(source => source.Coverage == LogCoverage.Received && !source.Shortened && !source.Dropped) &&
                    incomplete == 0,
                Records = records, IncompletePayloads = incomplete, Collecting = collecting, Store = Logs.Status,
                Collector = collected?.Collector, CollectedAt = collected?.CollectedAt,
                PayloadNotice = "Payloads can contain private model content. A missing file may have expired, been evicted, or failed capture."
            }, ExportJson, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is PeerFailure or IOException or UnauthorizedAccessException or JsonException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
            {
                context.Response.StatusCode = error is PeerFailure failure ? failure.Status : 500;
                await context.Response.WriteAsJsonAsync(new { error = "Mango could not export this log history." }, cancellation);
            }
        }
        finally { logExports.Release(); }
    }

    private async Task<bool> WriteZipPayloadAsync(ZipArchive zip, LogConfiguration config, LogWindow window, LogEntry entry,
        LogPayload payload, bool response, CancellationToken cancellation)
    {
        if (payload.Stored <= 0)
            return payload.Exact && payload.Bytes == 0;
        var request = new LogPayloadTransfer(config.Epoch, window.Day, config.Policy.TimeZone, entry.Node, entry.Id,
            entry.At, entry.Sequence, response, 0);
        var chunk = await Logs.ReadChunkAsync(request, window, cancellation);
        if (chunk is null)
            return false;
        var kind = response ? "response" : "request";
        var zipEntry = zip.CreateEntry($"payloads/{entry.Id:N}-{kind}.bin", CompressionLevel.Fastest);
        await using var stream = await zipEntry.OpenAsync(cancellation);
        long copied = 0;
        while (chunk is not null)
        {
            await stream.WriteAsync(chunk.Data, cancellation);
            copied += chunk.Data.Length;
            if (copied >= chunk.Length)
                break;
            chunk = await Logs.ReadChunkAsync(request with { Offset = copied }, window, cancellation);
        }
        return payload.Exact && copied == payload.Stored;
    }
}
