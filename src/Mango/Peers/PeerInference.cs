using System.Net;
using Mango.Providers;

namespace Mango.Peers;

internal sealed record InferenceAccess(Guid? KeyId, string? KeyName, InferenceScope[] Scopes, KeyPriority Priority,
    CancellationToken Cancelled)
{
    public bool Allows(InferenceScope scope) => KeyId is null || Scopes.Contains(scope);
    public bool Allows(InferenceOperation operation) => Allows((InferenceScope)operation);
    public static InferenceAccess? Of(HttpContext context) => context.Items[typeof(InferenceAccess)] as InferenceAccess;
}

internal sealed partial class PeerNetwork
{
    private static readonly Dictionary<string, InferenceOperation> InferenceOperations = Enum.GetValues<InferenceOperation>()
        .ToDictionary(InferencePaths.Path, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ActiveKey> active = [];
    private readonly object lookupLock = new();
    private readonly Dictionary<Guid, Task> lookups = [];
    private readonly Dictionary<Guid, DateTimeOffset> negatives = [];
    private readonly AttemptBudget lookupBudget = new(30, 5, PeerLimits.UnknownSources);

    private sealed class ActiveKey
    {
        public CancellationTokenSource Stop { get; } = new();
        public Task CancelWork { get; set; } = Task.CompletedTask;
        public int Count { get; set; }
    }

    private sealed class InferenceLease(PeerNetwork owner, Guid id, ActiveKey active, InferenceAccess access) : IDisposable
    {
        public InferenceAccess Access { get; } = access;
        public void Dispose() => owner.ReleaseLease(id, active);
    }

    public static bool IsInferenceRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && request.Path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase) ||
        HttpMethods.IsPost(request.Method) && request.Path.Value is { } path && InferenceOperations.ContainsKey(path);

    public async Task InferenceAsync(HttpContext context, RequestDelegate next, bool localSession)
    {
        var request = context.Request;
        var listing = HttpMethods.IsGet(request.Method) &&
            request.Path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase);
        InferenceOperation? operation = HttpMethods.IsPost(request.Method) && request.Path.Value is { } path &&
            InferenceOperations.TryGetValue(path, out var match) ? match : null;
        if (!listing && operation is null)
        {
            context.Response.StatusCode = 404;
            return;
        }
        if (localSession && !AllowsLocalInference || !localSession && IsMember && !Ready)
        {
            await InferenceError(context, 503, "Mango is reconciling network authority.");
            return;
        }
        var id = Guid.Empty;
        string? secret = null;
        if (!localSession)
        {
            if (!IsMember || !PeerCredentials.TryParseKey(PeerCredentials.Bearer(request), out id, out secret))
            {
                await InferenceError(context, 401, "The inference credential was rejected.");
                return;
            }
            var current = Snapshot;
            if (!current.Keys.ContainsKey(id) && current.Self is not null)
                await LookupAsync(id, InferenceSource.Parse(InferenceSource.Of(context).Address) ?? IPAddress.None, context.RequestAborted);
        }
        using var lease = AcquireLease(id, secret, operation, out var status);
        if (lease is null)
        {
            await InferenceError(context, status, status == 403
                ? "The inference credential does not allow this operation."
                : "The inference credential was rejected.");
            return;
        }
        context.Items[typeof(InferenceAccess)] = lease.Access;
        await next(context);
    }

    private InferenceLease? AcquireLease(Guid id, string? secret, InferenceOperation? operation, out int status, bool forwarded = false)
    {
        status = 401;
        lock (authorization)
        {
            var current = Snapshot;
            InferenceKey? key = null;
            if (id != Guid.Empty)
            {
                if (!Ready || current.Self is null || DeviceChanged(current) || !current.Keys.TryGetValue(id, out key) ||
                    key.Revoked || key.ExpiresAt <= clock.GetUtcNow() || !forwarded && !PeerCredentials.Verify(secret, key.Verifier))
                    return null;
                if (operation.HasValue && !key.Scopes.Contains((InferenceScope)operation.Value))
                {
                    status = 403;
                    return null;
                }
            }
            if (!active.TryGetValue(id, out var currentLease))
            {
                active[id] = currentLease = new();
                if (key?.ExpiresAt is { } expires && expires - clock.GetUtcNow() is { TotalMilliseconds: > 0 and < uint.MaxValue } remaining)
                    currentLease.Stop.CancelAfter(remaining);
            }
            currentLease.Count++;
            return new(this, id, currentLease, new(key?.Id, key?.Name, key?.Scopes ?? [], key?.Priority ?? KeyPriority.Normal,
                currentLease.Stop.Token));
        }
    }

    private void ReleaseLease(Guid id, ActiveKey lease)
    {
        lock (authorization)
        {
            if (--lease.Count != 0)
                return;
            if (active.GetValueOrDefault(id) == lease)
                active.Remove(id);
            if (lease.CancelWork.IsCompleted)
                lease.Stop.Dispose();
            else
                _ = lease.CancelWork.ContinueWith(_ => lease.Stop.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void AuthorityChanged(PeerSnapshot before, PeerSnapshot after)
    {
        var reset = before.Records.Identity?.Generation != after.Records.Identity?.Generation;
        var membershipLost = before.Self is not null && after.Self is null;
        lock (authorization)
        {
            if (reset || membershipLost || before.Records.Authority?.Revision != after.Records.Authority?.Revision)
                unlocks.Clear();
            foreach (var (id, lease) in active.ToArray())
            {
                if (!reset && !membershipLost && (id == Guid.Empty || after.Keys.TryGetValue(id, out var key) && !key.Revoked &&
                    (key.ExpiresAt is null || key.ExpiresAt > clock.GetUtcNow())))
                    continue;
                active.Remove(id);
                lease.CancelWork = lease.Stop.CancelAsync();
            }
        }
        if (reset || membershipLost)
        {
            providers.ClearPrivateExecution();
            contacts.Clear();
            mismatches.Clear();
            ClearSchedulingMemory();
            lock (lookupLock)
                negatives.Clear();
        }
        foreach (var node in before.Nodes.Values.Where(node => !node.Removed &&
                     (!after.Nodes.TryGetValue(node.Id, out var current) || current.Removed)))
            serving.CancelOrigin(node.Id);
        serving.Wake();
    }

    private async Task LookupAsync(Guid id, IPAddress source, CancellationToken cancellation)
    {
        Task pending;
        lock (lookupLock)
        {
            var now = clock.GetUtcNow();
            if (Snapshot.Keys.ContainsKey(id) || negatives.TryGetValue(id, out var expires) && expires > now)
                return;
            if (!lookups.TryGetValue(id, out pending!))
            {
                if (lookups.Count >= PeerLimits.UnknownConcurrent || !lookupBudget.Take(source, now))
                    return;
                lookups[id] = pending = LookupPeersAsync(id);
            }
        }
        await pending.WaitAsync(cancellation);
    }

    private async Task LookupPeersAsync(Guid id)
    {
        await Task.Yield();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        deadline.CancelAfter(PeerLimits.KeyLookupTimeout);
        var current = Snapshot;
        try
        {
            foreach (var peer in current.Others.Take(PeerLimits.UnknownPeers))
            {
                var result = await SendAsync<PeerKeyReply>(current, peer, $"key/{id:N}", null, deadline.Token);
                if (result?.Body?.Key is not { } key || key.Id != id || !PeerValidation.Valid(key))
                    continue;
                await MergeAsync(NetworkRecords.Empty with { Identity = current.Records.Identity, Keys = [key] }, deadline.Token);
                return;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or PeerFailure or IOException or UnauthorizedAccessException) { }
        finally
        {
            lock (lookupLock)
            {
                lookups.Remove(id);
                if (current.Records.Identity == Snapshot.Records.Identity && !Snapshot.Keys.ContainsKey(id))
                {
                    if (negatives.Count >= PeerLimits.UnknownNegative)
                        negatives.Remove(negatives.MinBy(entry => entry.Value).Key);
                    negatives[id] = clock.GetUtcNow() + PeerLimits.NegativeLifetime;
                }
            }
        }
    }

    private static Task InferenceError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { message, type = "authorization_error" } },
            context.RequestAborted);
    }
}
