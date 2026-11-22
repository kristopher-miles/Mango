using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Mango.Providers;

internal sealed class InferenceOutput(HttpContext context, InferenceOperation operation, string model, string? instanceHandle = null)
{
    private readonly byte[]? quotedHandle = instanceHandle is null ? null : Encoding.UTF8.GetBytes($"\"{instanceHandle}\"");
    private byte[]? quotedPublicName;
    private byte[]? fieldTail;
    private int depth;
    private bool inString, escaped, modelKey, modelValue;
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
        if (quotedHandle is null)
        {
            await WriteProtectedAsync(bytes, cancellation);
            return;
        }
        if (fieldTail is not null)
        {
            var joined = new byte[fieldTail.Length + bytes.Length];
            fieldTail.CopyTo(joined, 0);
            bytes.CopyTo(joined.AsMemory(fieldTail.Length));
            bytes = joined;
            fieldTail = null;
        }
        ArrayBufferWriter<byte>? changed = null;
        while (!bytes.IsEmpty)
        {
            var (offset, length) = FindModelField(bytes.Span);
            if (length == 0)
            {
                if (offset < bytes.Length) fieldTail = bytes[offset..].ToArray();
                if (changed is null)
                {
                    if (offset > 0) await WriteProtectedAsync(bytes[..offset], cancellation);
                    return;
                }
                changed.Write(bytes[..offset].Span);
                break;
            }
            changed ??= new(bytes.Length);
            changed.Write(bytes[..offset].Span);
            quotedPublicName ??= JsonSerializer.SerializeToUtf8Bytes(model);
            changed.Write(quotedPublicName);
            bytes = bytes[(offset + length)..];
        }
        if (changed is not null) await WriteProtectedAsync(changed.WrittenMemory, cancellation);
    }

    // A provider can echo its instance UUID in each SSE event. Only its top-level model field names the model.
    private (int Offset, int Length) FindModelField(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (value == '\\') escaped = true;
                else if (value == '"') inString = false;
                continue;
            }
            if (value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') continue;
            if (value == '"')
            {
                if (depth == 1)
                {
                    ReadOnlySpan<byte> token = modelValue ? quotedHandle : "\"model\""u8;
                    var remaining = bytes[i..];
                    var length = Math.Min(token.Length, remaining.Length);
                    if (remaining[..length].SequenceEqual(token[..length]))
                    {
                        if (length < token.Length) return (i, 0);
                        if (modelValue)
                        {
                            modelValue = false;
                            return (i, length);
                        }
                        modelKey = true;
                        i += length - 1;
                        continue;
                    }
                }
                inString = true;
            }
            else if (value is (byte)'{' or (byte)'[') depth++;
            else if (value is (byte)'}' or (byte)']') depth = Math.Max(0, depth - 1);
            modelValue = value == ':' && depth == 1 && modelKey;
            modelKey = false;
        }
        return (bytes.Length, 0);
    }

    private async Task WriteProtectedAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
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
        if (fieldTail is { } pending)
        {
            fieldTail = null;
            await WriteProtectedAsync(pending, cancellation);
        }
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
