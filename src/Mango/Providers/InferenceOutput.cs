using System.Text.Json;

namespace Mango.Providers;

internal sealed class InferenceOutput(HttpContext context, InferenceOperation operation, string model)
{
    private byte[]? tail;
    private byte[]? publicName;

    private bool Inspect => context.Response.StatusCode >= 400 ||
        (context.Response.ContentType is { } type
            ? !type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
                !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                (operation != InferenceOperation.Speech || type.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                    type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            : operation != InferenceOperation.Speech);

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        if (!Inspect)
        {
            await InferenceRequest.WriteOutputAsync(context, bytes, cancellation);
            return;
        }
        if (tail is not null)
        {
            var joined = new byte[tail.Length + bytes.Length];
            tail.CopyTo(joined, 0);
            bytes.CopyTo(joined.AsMemory(tail.Length));
            bytes = joined;
            tail = null;
        }
        while (!bytes.IsEmpty)
        {
            var (offset, matched) = Scan(bytes.Span);
            if (offset > 0)
                await InferenceRequest.WriteOutputAsync(context, bytes[..offset], cancellation);
            bytes = bytes[offset..];
            if (!matched)
            {
                if (!bytes.IsEmpty)
                    tail = bytes.ToArray();
                return;
            }
            publicName ??= JsonEncodedText.Encode(model).EncodedUtf8Bytes.ToArray();
            await InferenceRequest.WriteOutputAsync(context, publicName, cancellation);
            bytes = bytes[ProviderIdentity.InternalInstanceLength..];
        }
    }

    public async Task CompleteAsync(CancellationToken cancellation)
    {
        if (tail is { } remaining)
        {
            tail = null;
            await InferenceRequest.WriteOutputAsync(context, remaining, cancellation);
        }
    }

    // Retain only a possible identifier prefix when a transport splits it across writes.
    private static (int Offset, bool Matched) Scan(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> prefix = "mango-"u8;
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            var next = bytes[offset..].IndexOfAny((byte)'m', (byte)'M');
            if (next < 0)
                break;
            offset += next;
            var remaining = bytes[offset..];
            var prefixLength = Math.Min(prefix.Length, remaining.Length);
            var index = 0;
            while (index < prefixLength && (remaining[index] | 0x20) == prefix[index])
                index++;
            if (index != prefixLength)
                continue;

            var limit = Math.Min(ProviderIdentity.InternalInstanceLength, remaining.Length);
            while (index < limit && IsHex(remaining[index]))
                index++;
            if (index == limit)
                return (offset, limit == ProviderIdentity.InternalInstanceLength);
        }
        return (bytes.Length, false);
    }

    private static bool IsHex(byte value) => value is >= (byte)'0' and <= (byte)'9' || (value | 0x20) is >= 'a' and <= 'f';
}
