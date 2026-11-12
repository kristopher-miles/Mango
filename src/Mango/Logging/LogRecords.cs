using System.Text.Json.Serialization;
using Mango.Peers;
using Mango.Providers;

namespace Mango.Logging;

internal enum RequestLogMode { Off, Sparse, Verbose, Everything }
internal enum LogKind { Request, Attempt, Benchmark, Operation }
internal enum LogCoverage { Received, Unavailable, Off, Error, BeyondRetention }

internal sealed record LogPolicy(Revision Revision, RequestLogMode Mode, bool Operational, string TimeZone,
    int RawDays, int AggregateDays, int CapMiB)
{
    public static readonly LogPolicy Default = new(default, RequestLogMode.Sparse, true, LocalZone(), 7, 30, 1024);
    public static readonly LogPolicy ExistingNetworkDefault = Default with { TimeZone = "Etc/UTC" };
    public bool Valid => Enum.IsDefined(Mode) && RawDays is >= 1 and <= 365 && AggregateDays is >= 1 and <= 365 &&
        CapMiB is >= 1 and <= 16384 && Zone(TimeZone) is not null;

    public static TimeZoneInfo? Zone(string? id)
    {
        if (id is not { Length: > 0 and <= 128 } || !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
            return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    private static string LocalZone() => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var id)
        ? id : "Etc/UTC";
}

internal sealed record LogEpoch(Guid? Network, Revision? Generation);
internal sealed record LogConfiguration(LogEpoch Epoch, LogPolicy Policy, Guid Node, string? Name);
internal sealed record LogPayload(long Bytes, long Stored, bool Exact, string? MediaType);
internal sealed record LogRoute(Guid Attempt, Guid Node, string? Name, string Deployment, string Outcome, int Status,
    string? Reason, double? QueueMilliseconds, double? LoadMilliseconds);

internal sealed record LogEntry(Guid Id, LogKind Kind, DateTimeOffset At, Guid Node, string? NodeName,
    long Sequence = 0, Guid? Request = null, Guid? Attempt = null, Guid? Ingress = null, string? IngressName = null,
    Guid? Key = null, string? KeyName = null, InferenceSource? Source = null, string? Alias = null,
    string? Deployment = null, InferenceOperation? Task = null, KeyPriority? Priority = null,
    string? Outcome = null, string? Reason = null, int? Status = null, long? RequestBytes = null,
    long? ResponseBytes = null, int? ContextTokens = null, int? PromptTokens = null, int? OutputTokens = null,
    string? PromptCountSource = null,
    double? QueueMilliseconds = null, double? LoadMilliseconds = null, double? PreparationMilliseconds = null,
    double? ProviderHeadersMilliseconds = null, double? FirstOutputMilliseconds = null, double? FirstTokenMilliseconds = null,
    double? ElapsedMilliseconds = null,
    double? TokensPerSecond = null, int? Items = null, long? RequestedPixels = null, double? AudioSeconds = null,
    LogRoute[]? Routes = null, LogPayload? Input = null, LogPayload? Output = null,
    DateTimeOffset? CompletedAt = null);

internal sealed record LogWindow(DateOnly Day, DateTimeOffset Start, DateTimeOffset End)
{
    public static LogWindow For(DateOnly day, TimeZoneInfo zone) => new(day, Boundary(day, zone), Boundary(day.AddDays(1), zone));

    private static DateTimeOffset Boundary(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return zone.IsAmbiguousTime(local) ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime() :
            TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

internal sealed record LogPage(LogCoverage Coverage, LogEntry[] Entries, long Watermark, bool More, bool Shortened = false,
    bool Dropped = false);
internal sealed record LogSource(Guid Node, string Name, LogCoverage Coverage, long Watermark, DateTimeOffset ObservedAt,
    bool Shortened = false, bool Dropped = false, bool FromCache = false);
internal sealed record LogCollection(LogEpoch Epoch, DateOnly Day, string TimeZone, Guid Collector, DateTimeOffset CollectedAt, LogSource[] Sources);
internal sealed record LogChunk(long Length, byte[] Data);
internal sealed record LogTransfer(LogEpoch Epoch, DateOnly Day, string TimeZone, Guid Node, long After);
internal sealed record LogPayloadTransfer(LogEpoch Epoch, DateOnly Day, string TimeZone, Guid Node, Guid Event,
    DateTimeOffset At, long Sequence, bool Response, long Offset);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LogSettingsUpdate([property: JsonRequired] RequestLogMode Mode, [property: JsonRequired] bool Operational,
    string TimeZone, int RawDays, int AggregateDays, int CapMiB);
