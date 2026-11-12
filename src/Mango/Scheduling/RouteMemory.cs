using Mango.Providers;

namespace Mango.Scheduling;

internal sealed record RouteAffinity(Guid Request, Guid Identity, string Alias, InferenceOperation Operation, string? Session,
    string? Prefix, PlacementChoice Placement, DateTimeOffset Expires, Guid? Advice = null);

internal sealed class RouteMemory : IDisposable
{
    private const int MaxPerIdentity = 64;
    private const int MaxEntries = 2048;
    private readonly object sync = new();
    private readonly List<RouteAffinity> entries = [];
    private readonly TimeProvider clock;
    private readonly ITimer expiry;
    private bool disposed;

    public RouteMemory(TimeProvider? time = null)
    {
        clock = time ?? TimeProvider.System;
        expiry = clock.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public RouteAffinity? Find(Guid identity, InferenceShape shape, string? session, string[] prefixes, DateTimeOffset now)
    {
        lock (sync)
        {
            entries.RemoveAll(entry => entry.Expires <= now);
            return entries.Where(entry => entry.Identity == identity && entry.Operation == shape.Operation &&
                    string.Equals(entry.Alias, shape.Alias, StringComparison.OrdinalIgnoreCase) &&
                    (session is not null ? entry.Session == session : entry.Session is null &&
                        entry.Prefix is not null && prefixes.Contains(entry.Prefix)))
                .OrderByDescending(entry => session is null ? Array.IndexOf(prefixes, entry.Prefix) : 0)
                .ThenByDescending(entry => entry.Expires).FirstOrDefault();
        }
    }

    public void Remember(RouteAffinity value)
    {
        if (value.Session is null && value.Prefix is null)
            return;
        lock (sync)
        {
            entries.RemoveAll(entry => entry.Expires <= value.Expires.AddMinutes(-5) ||
                entry.Identity == value.Identity && entry.Operation == value.Operation &&
                string.Equals(entry.Alias, value.Alias, StringComparison.OrdinalIgnoreCase) &&
                (value.Session is not null ? value.Session == entry.Session : value.Prefix == entry.Prefix));
            var matching = entries.Where(entry => entry.Identity == value.Identity).ToArray();
            if (matching.Length >= MaxPerIdentity)
                entries.Remove(matching[0]);
            if (entries.Count >= MaxEntries)
                entries.RemoveAt(0);
            entries.Add(value);
            ScheduleExpiry();
        }
    }

    public void Advise(Guid request, Guid source, Guid destination)
    {
        lock (sync)
        {
            var index = entries.FindIndex(entry => entry.Request == request && entry.Placement.Node == source);
            if (index >= 0)
                entries[index] = entries[index] with { Advice = destination };
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
            ScheduleExpiry();
        }
    }

    private void Expire()
    {
        lock (sync)
        {
            entries.RemoveAll(entry => entry.Expires <= clock.GetUtcNow());
            ScheduleExpiry();
        }
    }

    private void ScheduleExpiry()
    {
        if (disposed)
            return;
        var delay = entries.Count == 0 ? Timeout.InfiniteTimeSpan : entries.Min(entry => entry.Expires) - clock.GetUtcNow();
        expiry.Change(entries.Count > 0 && delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            expiry.Dispose();
        }
    }
}
