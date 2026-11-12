using System.Net.Http.Headers;

namespace Mango.Logging;

internal sealed class LogCapture(LogStore store, LogConfiguration configuration, HttpContext context)
{
    private const long MaxCaptureBytes = 64L * 1024 * 1024;
    private readonly List<byte[]> input = [];
    private long inputBytes;
    private long outputBytes;
    private long reservedInput;
    private bool inputComplete;
    private bool inputAbandoned;
    private bool inputExcluded;
    private bool inputCounted;
    private bool outputComplete = true;
    private readonly bool wantsInput = configuration.Policy.Mode >= RequestLogMode.Verbose;
    private readonly bool wantsOutput = configuration.Policy.Mode == RequestLogMode.Everything;
    public Guid Id { get; } = Guid.NewGuid();
    public Mango.Peers.Revision? Generation => configuration.Epoch.Generation;
    public LogEntry? Entry { get; set; }
    public string? Failure { get; set; }
    public double? FirstOutputMilliseconds { get; private set; }
    public long Started { get; set; }
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public static LogCapture? Of(HttpContext context) => context.Items[typeof(LogCapture)] as LogCapture;

    public async Task RunAsync(Func<Task> next)
    {
        var request = context.Request.Body;
        var response = context.Response.Body;
        context.Items[typeof(LogCapture)] = this;
        context.Request.Body = new CaptureStream(request, this, response: false);
        context.Response.Body = new CaptureStream(response, this, response: true);
        try { await next(); }
        finally
        {
            AbandonInput();
            context.Request.Body = request;
            context.Response.Body = response;
        }
    }

    private void Read(ReadOnlySpan<byte> data)
    {
        inputBytes += data.Length;
        if (!wantsInput || data.IsEmpty || inputAbandoned)
            return;
        if (inputBytes > MaxCaptureBytes || !store.Reserve(data.Length))
        {
            AbandonInput();
            inputAbandoned = true;
            return;
        }
        reservedInput += data.Length;
        input.Add(data.ToArray());
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        FirstOutputMilliseconds ??= Clock.GetElapsedTime(Started).TotalMilliseconds;
        outputBytes += data.Length;
        if (!wantsOutput)
            return;
        if (!outputComplete || outputBytes > MaxCaptureBytes || !store.Reserve(data.Length))
        {
            outputComplete = false;
            store.DropBytes(data.Length);
        }
        else if (!store.QueuePayload(configuration.Epoch, Id, true, [data.ToArray()]))
            outputComplete = false;
    }

    public void AcceptedInput()
    {
        if (!wantsInput || inputAbandoned)
            return;
        if (context.Request.ContentLength is { } length && length != inputBytes ||
            context.Request.HasFormContentType && ContainsCredentialHeader(input))
        {
            inputExcluded = true;
            AbandonInput();
            inputAbandoned = true;
            return;
        }
        inputComplete = input.Count == 0 || store.QueuePayload(configuration.Epoch, Id, false, input.ToArray());
        inputCounted = !inputComplete;
        reservedInput = 0;
        input.Clear();
    }

    public void Finish(LogEntry fallback)
    {
        var entry = Entry ?? fallback;
        var aborted = context.RequestAborted.IsCancellationRequested;
        if (wantsInput && !inputComplete && !inputCounted && !inputExcluded)
            store.DropBytes(inputBytes);
        store.Queue(configuration, entry with
        {
            Status = aborted && !context.Response.HasStarted ? null : context.Response.StatusCode,
            Outcome = entry.Outcome ?? (aborted ? "cancelled" : context.Response.StatusCode < 400 ? "completed" : "rejected"),
            Reason = Failure ?? entry.Reason,
            ElapsedMilliseconds = Clock.GetElapsedTime(Started).TotalMilliseconds,
            FirstOutputMilliseconds = FirstOutputMilliseconds,
            CompletedAt = Clock.GetUtcNow(),
            RequestBytes = inputBytes == 0 ? context.Request.ContentLength : inputBytes,
            ResponseBytes = outputBytes,
            Input = wantsInput ? new(inputBytes, 0, inputComplete, MediaType(context.Request.ContentType)) : null,
            Output = wantsOutput ? new(outputBytes, 0, outputComplete && !aborted, MediaType(context.Response.ContentType)) : null
        });
    }

    private void AbandonInput()
    {
        if (reservedInput == 0)
            return;
        store.Release(reservedInput);
        reservedInput = 0;
        input.Clear();
    }

    private static string? MediaType(string? value)
    {
        if (!MediaTypeHeaderValue.TryParse(value, out var parsed))
            return null;
        var boundary = parsed.MediaType?.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true
            ? parsed.Parameters.FirstOrDefault(item => item.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value : null;
        return boundary is { Length: > 0 and <= 130 } && !boundary.Any(char.IsControl)
            ? $"{parsed.MediaType}; boundary={boundary}" : parsed.MediaType;
    }

    private static readonly string[] CredentialHeaders =
        ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key",
         "mango-admin", "mango-session", "x-provider-token", "x-auth-token"];

    private static bool ContainsCredentialHeader(List<byte[]> blocks)
    {
        Span<char> name = stackalloc char[32];
        var used = 0;
        var readName = true;
        foreach (var block in blocks)
            foreach (var value in block)
            {
                if (value == (byte)'\n')
                {
                    used = 0;
                    readName = true;
                }
                else if (readName)
                {
                    if (value == (byte)':')
                    {
                        foreach (var header in CredentialHeaders)
                            if (name[..used].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                                return true;
                        readName = false;
                    }
                    else if (used < name.Length)
                        name[used++] = (char)value;
                    else
                        readName = false;
                }
            }
        return false;
    }

    private sealed class CaptureStream(Stream inner, LogCapture capture, bool response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (!response)
                capture.Read(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (!response)
                capture.Read(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            if (response)
                capture.Write(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            if (response)
                capture.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
