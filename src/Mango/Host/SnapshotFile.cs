using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mango.Host;

internal sealed class SnapshotFile<T>(string path, Func<T, string?> validate) where T : class
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly SemaphoreSlim writer = new(1);
    private readonly string lastGood = path + ".last-good";
    private readonly string pending = path + ".pending";
    private bool primaryUnreadable;
    private T? value;

    public T Value => Volatile.Read(ref value) ?? throw new InvalidOperationException("Snapshot has not been loaded.");
    public string? RecoveryWarning { get; private set; }

    public async Task LoadAsync(T defaults, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            var (primary, primaryProblem) = await ReadAsync(path, cancellationToken);
            if (primary is not null)
            {
                Volatile.Write(ref value, primary);
                return;
            }

            var (backup, backupProblem) = await ReadAsync(lastGood, cancellationToken);
            if (backup is not null)
            {
                primaryUnreadable = primaryProblem is not null;
                RecoveryWarning = $"Loaded the last good {Path.GetFileName(path)}. {primaryProblem ?? "The primary file is missing."}";
                Volatile.Write(ref value, backup);
                return;
            }

            if (primaryProblem is not null || backupProblem is not null)
                throw new InvalidDataException($"No usable snapshot for {path}. {primaryProblem} {backupProblem}");

            await WriteAsync(defaults, cancellationToken);
        }
        finally
        {
            writer.Release();
        }
    }

    public async Task<T> UpdateAsync(Func<T, T> update, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            var next = update(Value);
            if (!EqualityComparer<T>.Default.Equals(Value, next))
                await WriteAsync(next, cancellationToken);
            return Value;
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task<(T? Value, string? Problem)> ReadAsync(string source, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var read = await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
            if (read is null)
                return (null, $"{source} is empty.");
            var error = validate(read);
            return error is null ? (read, null) : (null, $"{source}: {error}");
        }
        catch (FileNotFoundException)
        {
            return (null, null);
        }
        catch (JsonException error)
        {
            return (null, $"{source} contains invalid JSON ({error.Path ?? "root"}).");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, $"Cannot read {source}: {error.Message}");
        }
    }

    private async Task WriteAsync(T next, CancellationToken cancellationToken)
    {
        if (validate(next) is { } error)
            throw new ArgumentException(error);

        await using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, next, Json, cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            var backup = primaryUnreadable ? path + $".invalid-{Guid.NewGuid():N}" : lastGood;
            File.Replace(pending, path, backup);
            if (primaryUnreadable)
                RecoveryWarning = $"The original unreadable snapshot was retained as {backup}.";
        }
        else
        {
            File.Move(pending, path);
        }

        primaryUnreadable = false;
        Volatile.Write(ref value, next);
    }
}
