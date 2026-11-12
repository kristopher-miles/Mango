using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mango.Logging;

internal sealed partial class LogStore
{
    public async Task<LogPage> ReadPageAsync(LogEpoch epoch, LogWindow window, Guid node, long after,
        CancellationToken cancellation, bool rawOnly = false)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(epoch);
            var config = Configuration;
            var local = node == config.Node || node == Guid.Empty;
            var limit = rawOnly ? config.Policy.RawDays : Math.Max(config.Policy.RawDays, config.Policy.AggregateDays);
            if (window.End < clock.GetUtcNow().AddDays(-limit))
                return new(LogCoverage.BeyondRetention, [], after, false, shortened);
            IEnumerable<LogEntry> Select(string partition) => ReadPartition(partition, after)
                .Where(entry => entry.Node == node && entry.At >= window.Start && entry.At < window.End).Take(PageSize + 1);
            var entries = UtcDays(window).SelectMany(day =>
                    (local ? Select(Partition(day, true, node)) : []).Concat(rawOnly ? [] : Select(Partition(day, false, node))))
                .DistinctBy(entry => entry.Sequence).OrderBy(entry => entry.Sequence).Take(PageSize + 1).ToArray();
            var more = entries.Length > PageSize;
            if (more)
                entries = entries[..PageSize];
            RequireEpoch(epoch);
            var coverage = local && config.Policy.Mode == RequestLogMode.Off ? LogCoverage.Off :
                entries.Length == 0 && after == 0 && window.End < clock.GetUtcNow().AddDays(-config.Policy.RawDays)
                    ? LogCoverage.BeyondRetention : LogCoverage.Received;
            return new(coverage, entries, entries.LastOrDefault()?.Sequence ?? after, more, shortened, HasDrops);
        }
        finally { disk.Release(); }
    }

    public async Task<LogChunk?> ReadChunkAsync(LogPayloadTransfer transfer, LogWindow window, CancellationToken cancellation)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(transfer.Epoch);
            if (transfer.At < window.Start || transfer.At >= window.End ||
                transfer.At < clock.GetUtcNow().AddDays(-Configuration.Policy.RawDays - 1))
                return null;
            var partition = Partition(DayKey(transfer.At), transfer.Node == Configuration.Node || transfer.Node == Guid.Empty, transfer.Node);
            var segment = Segments(partition).LastOrDefault(item => item.Sequence <= transfer.Sequence).Path;
            if (segment is null)
                return null;
            var path = PayloadPath(segment, transfer.Event, transfer.Response);
            var length = Length(path);
            if (transfer.Offset < 0 || transfer.Offset >= length)
                return null;
            await using var file = OpenRead(path);
            file.Position = transfer.Offset;
            var data = new byte[(int)Math.Min(ChunkBytes, length - transfer.Offset)];
            await file.ReadExactlyAsync(data, cancellation);
            RequireEpoch(transfer.Epoch);
            return new(length, data);
        }
        finally { disk.Release(); }
    }

    public async Task<bool> StageChunkAsync(LogEpoch epoch, Guid id, bool response, long offset, byte[] data, CancellationToken cancellation)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(epoch);
            var path = PayloadPath(Pending, id, response);
            if (Length(path) != offset || !await PruneAsync(Configuration.Policy, data.Length, cancellation, path))
                return false;
            Directory.CreateDirectory(Pending);
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
                await stream.WriteAsync(data, cancellation);
            }
            finally { Interlocked.Add(ref bytes, Length(path) - offset); }
            return true;
        }
        finally { disk.Release(); }
    }

    public async Task ImportAsync(LogEpoch epoch, Guid node, LogEntry[] entries, CancellationToken cancellation,
        bool metadataOnly = false)
    {
        if (entries.Length == 0 || node == Configuration.Node && !metadataOnly)
            return;
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(epoch);
            var policy = Configuration.Policy;
            foreach (var day in entries.GroupBy(entry => DayKey(entry.At)))
            {
                var partition = Partition(day.Key, false, node);
                var first = day.Min(entry => entry.Sequence);
                var last = day.Max(entry => entry.Sequence);
                var known = ReadPartition(partition, first - 1).TakeWhile(entry => entry.Sequence <= last)
                    .Select(entry => entry.Sequence).ToHashSet();
                var tail = Segments(partition).LastOrDefault().Path;
                var newest = tail is null ? 0 : ReadSegment(tail).LastOrDefault()?.Sequence ?? 0;
                foreach (var item in day.OrderBy(entry => entry.Sequence))
                {
                    var entry = metadataOnly ? item : item with
                    {
                        Input = PreparePayload(item.Id, false, item.Input),
                        Output = PreparePayload(item.Id, true, item.Output)
                    };
                    if (!known.Add(entry.Sequence))
                    {
                        DeleteFile(PayloadPath(Pending, entry.Id, false));
                        DeleteFile(PayloadPath(Pending, entry.Id, true));
                        continue;
                    }
                    var earlier = entry.Sequence < newest;
                    var segment = earlier ? Segments(partition).LastOrDefault(item => item.Sequence <= entry.Sequence).Path ??
                        Path.Combine(partition, SegmentName(entry.Sequence)) : CurrentSegment(partition, entry.Sequence);
                    var line = Encode(entry);
                    var path = Path.Combine(segment, "events.ndjson");
                    var rewrite = earlier ? ReadSegment(segment).Append(entry).OrderBy(item => item.Sequence).ToArray() : [];
                    var additional = rewrite.Length > 1 ? Length(path) + line.Length : line.Length;
                    if (!await PruneAsync(policy, additional, cancellation, segment))
                        throw new IOException("The log byte cap prevented an aggregate write.");
                    Directory.CreateDirectory(segment);
                    if (!metadataOnly)
                    {
                        MovePayload(entry.Id, false, segment, entry.Input);
                        MovePayload(entry.Id, true, segment, entry.Output);
                    }
                    if (rewrite.Length > 1)
                        await RewriteSegmentAsync(path, rewrite, cancellation);
                    else
                        await AppendAsync(path, line, cancellation);
                    newest = Math.Max(newest, entry.Sequence);
                }
            }
        }
        finally { disk.Release(); }
    }

    public async Task<LogCollection?> ReadCollectionAsync(LogEpoch epoch, DateOnly day, string zone, CancellationToken cancellation)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(epoch);
            var path = CoveragePath(day, zone);
            if (!File.Exists(path))
                return null;
            await using var file = OpenRead(path);
            var collection = await JsonSerializer.DeserializeAsync<LogCollection>(file, Json, cancellation);
            RequireEpoch(epoch);
            return collection?.Epoch == epoch ? collection : null;
        }
        finally { disk.Release(); }
    }

    public async Task SaveCollectionAsync(LogCollection collection, CancellationToken cancellation)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            RequireEpoch(collection.Epoch);
            var path = CoveragePath(collection.Day, collection.TimeZone);
            var data = Encode(collection);
            var difference = data.Length - Length(path);
            if (!await PruneAsync(Configuration.Policy, data.Length, cancellation, path))
                throw new IOException("The log byte cap prevented a coverage update.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, data, cancellation);
                File.Move(temp, path, overwrite: true);
                Interlocked.Add(ref bytes, difference);
            }
            finally { File.Delete(temp); }
        }
        finally { disk.Release(); }
    }

    public Guid[] AggregateNodes(LogWindow window) => UtcDays(window)
        .SelectMany(day => Folders(Path.Combine(root, "aggregate", day)))
        .Select(folder => Guid.TryParseExact(Path.GetFileName(folder), "N", out var node) ? node : Guid.Empty)
        .Where(node => node != Guid.Empty).Distinct().ToArray();

    private string CoveragePath(DateOnly day, string zone)
    {
        var zoneKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(zone)))[..16];
        return Path.Combine(root, "coverage", $"{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{zoneKey}.json");
    }

    private string Partition(string day, bool local, Guid node) => local
        ? Path.Combine(root, node == Guid.Empty && Configuration.Node != Guid.Empty ? "standalone" : "raw", day)
        : Path.Combine(root, "aggregate", day, node.ToString("N"));

    private async Task RewriteSegmentAsync(string path, LogEntry[] entries, CancellationToken cancellation)
    {
        var previous = Length(path);
        var temp = path + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
                foreach (var entry in entries)
                    await file.WriteAsync(Encode(entry), cancellation);
            File.Move(temp, path, overwrite: true);
            Interlocked.Add(ref bytes, Length(path) - previous);
        }
        finally { File.Delete(temp); }
    }

    private static IEnumerable<string> UtcDays(LogWindow window)
    {
        for (var day = window.Start; day.UtcDateTime.Date < window.End.UtcDateTime.Date.AddDays(1); day = day.AddDays(1))
            yield return DayKey(day);
    }

    private static (long Sequence, string Path)[] Segments(string partition) => Folders(partition).Select(path =>
            (Sequence: long.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : 0, Path: path))
        .Where(item => item.Sequence > 0).OrderBy(item => item.Sequence).ToArray();

    private static IEnumerable<LogEntry> ReadPartition(string partition, long after)
    {
        var segments = Segments(partition);
        var start = Math.Max(0, Array.FindLastIndex(segments, item => item.Sequence <= after));
        for (var i = start; i < segments.Length; i++)
            foreach (var entry in ReadSegment(segments[i].Path))
                if (entry.Sequence > after)
                    yield return entry;
    }

    private static IEnumerable<LogEntry> ReadSegment(string segment)
    {
        var path = Path.Combine(segment, "events.ndjson");
        if (!File.Exists(path))
            yield break;
        using var file = OpenRead(path);
        if (file.Length == 0)
            yield break;
        file.Seek(-1, SeekOrigin.End);
        var terminated = file.ReadByte() == '\n';
        file.Position = 0;
        using var reader = new StreamReader(file, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (!terminated && reader.EndOfStream)
                break;
            LogEntry? entry;
            try { entry = JsonSerializer.Deserialize<LogEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry is not null)
                yield return entry;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32 * 1024, FileOptions.Asynchronous);

    private void RequireEpoch(LogEpoch epoch)
    {
        if (!Available(epoch))
            throw new Mango.Peers.PeerFailure(409, "This log generation is unavailable or has been cleared.");
    }
}
