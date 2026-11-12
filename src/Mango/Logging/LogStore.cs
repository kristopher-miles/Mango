using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Mango.Host;

namespace Mango.Logging;

internal sealed partial class LogStore : IAsyncDisposable
{
    private const long MemoryLimit = 96L * 1024 * 1024;
    private const int SegmentBytes = 256 * 1024;
    public const int PageSize = 48;
    public const int ChunkBytes = 64 * 1024;
    private const long SequenceBlock = 1_000_000;
    private const string StaleGeneration = "Local logs have a newer deletion generation. Refresh the network before reading them.";
    private static readonly JsonSerializerOptions Json = SnapshotFile<Journal>.Json;
    private readonly string directory;
    private readonly string root;
    private readonly SnapshotFile<Journal> journal;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim disk = new(1);
    private readonly Channel<Write> writes = Channel.CreateBounded<Write>(new BoundedChannelOptions(512)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task writer;
    private readonly Func<CancellationToken, Task>? beforeWrite;
    private readonly CancellationTokenSource stop = new();
    private LogConfiguration current = new(new(null, null), LogPolicy.Default, Guid.Empty, null);
    private LogEpoch? applied;
    private bool journalLoaded;
    private long sequence;
    private long bytes;
    private long pendingBytes;
    private long droppedBytes;
    private long droppedRecords;
    private string? failure;
    private bool shortened;
    private LogPolicy? prunedPolicy;
    private DateTimeOffset pruneAfter;

    private sealed record Journal(LogEpoch? Epoch, long Reserved, bool Shortened = false);
    private sealed record Write(LogEpoch? Epoch = null, Guid Event = default, bool Response = false,
        byte[][]? Blocks = null, LogEntry? Entry = null);

    public LogStore(string directory, TimeProvider clock, Func<CancellationToken, Task>? beforeWrite = null)
    {
        this.directory = directory;
        root = Path.Combine(directory, "logs", "journal");
        journal = new(Path.Combine(directory, "log-generation.json"), value =>
            value.Reserved is >= 0 and < long.MaxValue - SequenceBlock ? null : "The log sequence is invalid.");
        this.clock = clock;
        this.beforeWrite = beforeWrite;
        writer = RunAsync();
    }

    public event Action? Applied;
    public LogConfiguration Configuration => Volatile.Read(ref current);
    public LogEpoch? AppliedEpoch => Volatile.Read(ref applied);
    public bool Available(LogEpoch epoch) => Configuration.Epoch == epoch && AppliedEpoch == epoch;
    public bool HasDrops => Interlocked.Read(ref droppedRecords) > 0 || Interlocked.Read(ref droppedBytes) > 0;
    public object Status => new { Bytes = Interlocked.Read(ref bytes), Shortened = Volatile.Read(ref shortened),
        DroppedBytes = Interlocked.Read(ref droppedBytes), DroppedRecords = Interlocked.Read(ref droppedRecords),
        Error = Volatile.Read(ref failure) };

    public async Task InitializeAsync(LogConfiguration configuration)
    {
        Volatile.Write(ref current, configuration);
        await PrepareAsync(CancellationToken.None, recover: true);
    }

    public void Configure(LogConfiguration configuration)
    {
        var previous = Configuration;
        Volatile.Write(ref current, configuration);
        if (previous.Epoch != configuration.Epoch || previous.Policy != configuration.Policy)
            writes.Writer.TryWrite(new());
    }

    public bool Reserve(int count)
    {
        if (Interlocked.Add(ref pendingBytes, count) <= MemoryLimit)
            return true;
        Release(count);
        return false;
    }

    public void Release(long count) => Interlocked.Add(ref pendingBytes, -count);
    public void DropBytes(long count) => Interlocked.Add(ref droppedBytes, count);

    public bool QueuePayload(LogEpoch epoch, Guid id, bool response, byte[][] blocks)
    {
        if (Available(epoch) && writes.Writer.TryWrite(new(epoch, id, response, blocks)))
            return true;
        var size = blocks.Sum(block => (long)block.Length);
        Release(size);
        DropBytes(size);
        return false;
    }

    public void Queue(LogConfiguration configuration, LogEntry entry)
    {
        if (!Available(configuration.Epoch) || !writes.Writer.TryWrite(new(configuration.Epoch, entry.Id, Entry: entry)))
            Interlocked.Increment(ref droppedRecords);
    }

    public async Task<bool> PrepareAsync(CancellationToken cancellation, bool recover = false)
    {
        await disk.WaitAsync(cancellation);
        try
        {
            await PrepareLockedAsync(cancellation, recover);
            return Available(Configuration.Epoch);
        }
        catch (Exception error) when (StorageError(error))
        {
            Volatile.Write(ref failure, "Mango could not update local log storage. Check disk space and permissions.");
            return false;
        }
        finally { disk.Release(); }
    }

    private async Task PrepareLockedAsync(CancellationToken cancellation, bool recover = false)
    {
        if (!journalLoaded)
        {
            Directory.CreateDirectory(directory);
            await journal.LoadAsync(new(null, 0), cancellation);
            sequence = journal.Value.Reserved + (journal.RecoveryWarning is null ? 0 : SequenceBlock);
            shortened = journal.Value.Shortened;
            journalLoaded = true;
        }
        var configuration = Configuration;
        var epoch = configuration.Epoch;
        if (AppliedEpoch != epoch)
        {
            var state = journal.Value;
            if (state.Epoch != epoch)
            {
                if (state.Epoch is { Generation: { } previous } persisted && persisted.Network == epoch.Network &&
                    (epoch.Generation is null || epoch.Generation.Value.CompareTo(previous) < 0))
                {
                    Volatile.Write(ref failure, StaleGeneration);
                    return;
                }
                var firstEnrollment = state.Epoch is { Network: null } && epoch.Network is not null;
                if (firstEnrollment)
                {
                    var raw = Path.Combine(root, "raw");
                    if (Directory.Exists(raw))
                        Directory.Move(raw, Path.Combine(root, "standalone"));
                }
                else if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
                await journal.UpdateAsync(_ => new(epoch, state.Reserved, firstEnrollment && state.Shortened), cancellation);
                Volatile.Write(ref shortened, journal.Value.Shortened);
                prunedPolicy = null;
                Interlocked.Exchange(ref droppedRecords, 0);
                Interlocked.Exchange(ref droppedBytes, 0);
            }
            if (Directory.Exists(Pending))
                Directory.Delete(Pending, recursive: true);
            if (Directory.Exists(root))
                foreach (var temp in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
                    File.Delete(temp);
            Interlocked.Exchange(ref bytes, Size(root));
            await PruneAsync(configuration.Policy, 0, cancellation);
            Volatile.Write(ref failure, journal.RecoveryWarning);
            Volatile.Write(ref applied, epoch);
            Applied?.Invoke();
        }
        else if (recover)
            Interlocked.Exchange(ref bytes, Size(root));
        if (Volatile.Read(ref failure) == StaleGeneration)
            Volatile.Write(ref failure, null);
        await PruneAsync(configuration.Policy, 0, cancellation);
    }

    private async Task RunAsync()
    {
        await foreach (var write in writes.Reader.ReadAllAsync())
        {
            var size = write.Blocks?.Sum(block => (long)block.Length) ?? 0;
            try
            {
                if (beforeWrite is not null)
                    await beforeWrite(stop.Token);
                await disk.WaitAsync(stop.Token);
                try
                {
                    if (write.Epoch is null || AppliedEpoch != Configuration.Epoch)
                        await PrepareLockedAsync(stop.Token);
                    if (write.Epoch is not { } epoch || !Available(epoch))
                        continue;
                    var policy = Configuration.Policy;
                    if (write.Blocks is { } blocks)
                        await WritePayloadAsync(write, blocks, size, policy, stop.Token);
                    else if (write.Entry is { } entry)
                        await WriteEntryAsync(entry, policy, stop.Token);
                }
                finally { disk.Release(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (StorageError(error))
            {
                if (write.Entry is not null)
                    Interlocked.Increment(ref droppedRecords);
                DropBytes(size);
                Volatile.Write(ref failure, "A log write failed. Some records or payload bytes are incomplete.");
            }
            finally { Release(size); }
        }
    }

    private async Task WritePayloadAsync(Write write, byte[][] blocks, long size, LogPolicy policy, CancellationToken cancellation)
    {
        var path = PayloadPath(Pending, write.Event, write.Response);
        if (!await PruneAsync(policy, size, cancellation, path))
        {
            DropBytes(size);
            return;
        }
        Directory.CreateDirectory(Pending);
        var before = Length(path);
        try
        {
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                32 * 1024, FileOptions.Asynchronous);
            foreach (var block in blocks)
                await file.WriteAsync(block, cancellation);
        }
        finally { Interlocked.Add(ref bytes, Length(path) - before); }
    }

    private async Task WriteEntryAsync(LogEntry entry, LogPolicy policy, CancellationToken cancellation)
    {
        if (sequence >= journal.Value.Reserved)
            await journal.UpdateAsync(value => value with { Reserved = sequence + SequenceBlock }, cancellation);
        var day = DayKey(entry.At);
        var partition = Path.Combine(root, "raw", day);
        var seq = ++sequence;
        var segment = CurrentSegment(partition, seq);
        entry = entry with
        {
            Sequence = seq,
            Input = PreparePayload(entry.Id, false, entry.Input),
            Output = PreparePayload(entry.Id, true, entry.Output)
        };
        var line = Encode(entry);
        if (!await PruneAsync(policy, line.Length, cancellation, segment))
        {
            DeleteFile(PayloadPath(Pending, entry.Id, false));
            DeleteFile(PayloadPath(Pending, entry.Id, true));
            Interlocked.Increment(ref droppedRecords);
            return;
        }
        Directory.CreateDirectory(segment);
        MovePayload(entry.Id, false, segment, entry.Input);
        MovePayload(entry.Id, true, segment, entry.Output);
        await AppendAsync(Path.Combine(segment, "events.ndjson"), line, cancellation);
    }

    private LogPayload? PreparePayload(Guid id, bool response, LogPayload? payload)
    {
        if (payload is null)
            return null;
        var length = Length(PayloadPath(Pending, id, response));
        return payload with { Stored = length, Exact = payload.Exact && length == payload.Bytes };
    }

    private void MovePayload(Guid id, bool response, string target, LogPayload? payload)
    {
        var pending = PayloadPath(Pending, id, response);
        if (payload?.Stored > 0 && File.Exists(pending))
            File.Move(pending, PayloadPath(target, id, response), overwrite: true);
        else
            DeleteFile(pending);
    }

    private string Pending => Path.Combine(root, "pending");
    private static string PayloadPath(string folder, Guid id, bool response) => Path.Combine(folder, $"{id:N}-{(response ? "out" : "in")}.bin");
    private static string DayKey(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string SegmentName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture);
    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static IEnumerable<string> Folders(string path) => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];
    private static IEnumerable<string> Files(string path, string pattern) => Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern) : [];
    private static long Size(string path) => Directory.Exists(path) ?
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(Length) : 0;
    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
    private static byte[] Encode<T>(T value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, LineJson) + "\n");
    private static readonly JsonSerializerOptions LineJson = new(Json) { WriteIndented = false };

    private static string CurrentSegment(string partition, long seq)
    {
        var last = Folders(partition).Order(StringComparer.Ordinal).LastOrDefault();
        return last is null || Length(Path.Combine(last, "events.ndjson")) >= SegmentBytes
            ? Path.Combine(partition, SegmentName(seq)) : last;
    }

    private async Task AppendAsync(string path, byte[] line, CancellationToken cancellation)
    {
        var before = Length(path);
        try
        {
            await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                32 * 1024, FileOptions.Asynchronous);
            file.Seek(0, SeekOrigin.End);
            while (file.Position > 0)
            {
                file.Seek(-1, SeekOrigin.Current);
                if (file.ReadByte() == '\n')
                    break;
                file.Seek(-1, SeekOrigin.Current);
            }
            if (file.Position != before)
                Interlocked.Increment(ref droppedRecords);
            file.SetLength(file.Position);
            await file.WriteAsync(line, cancellation);
            await file.FlushAsync(cancellation);
        }
        finally { Interlocked.Add(ref bytes, Length(path) - before); }
    }

    private async Task<bool> PruneAsync(LogPolicy policy, long additional, CancellationToken cancellation, string? keep = null)
    {
        var cap = (long)policy.CapMiB * 1024 * 1024;
        if (additional > cap)
            return false;
        var now = clock.GetUtcNow();
        var rawCutoff = DayKey(now.AddDays(-policy.RawDays));
        var aggregateCutoff = DayKey(now.AddDays(-policy.AggregateDays));
        if (policy != prunedPolicy || now >= pruneAfter)
        {
            foreach (var date in RawDays().Where(path => StringComparer.Ordinal.Compare(Path.GetFileName(path), rawCutoff) < 0))
                DeleteFolder(date);
            foreach (var date in Folders(Path.Combine(root, "aggregate")))
            {
                var name = Path.GetFileName(date);
                if (StringComparer.Ordinal.Compare(name, aggregateCutoff) < 0)
                    DeleteFolder(date);
                else if (StringComparer.Ordinal.Compare(name, rawCutoff) < 0)
                    foreach (var file in Directory.EnumerateFiles(date, "*.bin", SearchOption.AllDirectories))
                        DeleteFile(file);
            }
            foreach (var file in Files(Path.Combine(root, "coverage"), "*.json"))
                if (StringComparer.Ordinal.Compare(Path.GetFileName(file), aggregateCutoff) < 0)
                    DeleteFile(file);
            foreach (var path in Files(Pending, "*.bin"))
                if (File.GetLastWriteTimeUtc(path) < now.AddHours(-2).UtcDateTime)
                    DeleteFile(path);
            pruneAfter = now.AddHours(1);
            prunedPolicy = policy;
        }
        if (bytes + additional <= cap)
            return true;
        var segments = RawDays().SelectMany(date => Folders(date).Select(folder => (Day: Path.GetFileName(date), Path: folder, File: false)))
            .Concat(Folders(Path.Combine(root, "aggregate")).SelectMany(date =>
                Folders(date).SelectMany(node => Folders(node).Select(folder => (Day: Path.GetFileName(date), Path: folder, File: false)))))
            .Concat(Files(Path.Combine(root, "coverage"), "*.json").Select(file => (Day: Path.GetFileName(file)[..8], Path: file, File: true)))
            .Concat(Files(Pending, "*.bin").Select(file => (Day: DayKey(File.GetLastWriteTimeUtc(file)), Path: file, File: true)))
            .OrderBy(item => item.Day, StringComparer.Ordinal).ThenBy(item => Path.GetFileName(item.Path), StringComparer.Ordinal).ToArray();
        foreach (var (_, path, file) in segments)
        {
            cancellation.ThrowIfCancellationRequested();
            if (path == keep)
                continue;
            if (file)
            {
                if (Path.GetDirectoryName(path) == Pending)
                    DropBytes(Length(path));
                DeleteFile(path);
            }
            else
                DeleteFolder(path);
            if (bytes + additional <= cap)
                break;
        }
        if (!shortened)
        {
            Volatile.Write(ref shortened, true);
            await journal.UpdateAsync(value => value with { Shortened = true }, cancellation);
        }
        return bytes + additional <= cap;
    }

    private IEnumerable<string> RawDays() => Folders(Path.Combine(root, "raw")).Concat(Folders(Path.Combine(root, "standalone")));

    private void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        var size = Length(path);
        File.Delete(path);
        Interlocked.Add(ref bytes, -size);
    }

    private void DeleteFolder(string path)
    {
        var size = Size(path);
        Directory.Delete(path, recursive: true);
        Interlocked.Add(ref bytes, -size);
    }

    public async ValueTask DisposeAsync()
    {
        writes.Writer.TryComplete();
        stop.CancelAfter(TimeSpan.FromSeconds(3));
        await writer;
        stop.Dispose();
        disk.Dispose();
    }
}
