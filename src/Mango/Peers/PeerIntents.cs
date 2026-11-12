using System.Text.Json.Serialization;
using Mango.Providers;

namespace Mango.Peers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SharedModelUpdate(Guid Owner, string Deployment, Revision? Revision,
    string? Alias, Dictionary<string, int?>? Ratings, ModelServing? Serving);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SharedProviderUpdate(Guid Owner, Guid? Id, Revision? Revision,
    [property: JsonRequired] ProviderProfile Profile, string? Address, [property: JsonRequired] bool Enabled);

internal sealed partial class PeerNetwork
{
    private readonly SemaphoreSlim intentGate = new(1);
    private volatile bool intentWaitingForProvider;

    private void ProviderSettled()
    {
        if (intentWaitingForProvider)
            QueueCatalog();
    }

    public async Task<PushResult> SetModelIntentAsync(HttpRequest request, SharedModelUpdate input, CancellationToken cancellation,
        bool local = false)
    {
        var alias = input.Alias?.Trim();
        if (input.Owner == Guid.Empty || input.Deployment is not { Length: > 0 and <= 128 } ||
            !PeerValidation.ValidModelSettings(alias, input.Ratings, input.Serving))
            throw new PeerFailure(400, "Enter a valid alias, capability ratings, and serving limits.");
        var id = PeerValidation.ModelId(input.Owner, input.Deployment);
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            if (!current.Nodes.TryGetValue(input.Owner, out var owner) || owner.Removed)
                throw new PeerFailure(404, "Select a model on an enrolled node.");
            var existing = current.Records.Models.FirstOrDefault(model => model.Id == id && !model.Removed);
            if (existing is null && (!local || input.Owner != self.Id || !providers.View.Models.Any(model => model.Id == input.Deployment)))
                throw new PeerFailure(404, "The shared deployment was not found.");
            var justPublished = local && input.Revision is null && existing is not null &&
                providers.View.Models.FirstOrDefault(model => model.Id == input.Deployment) is { } observed &&
                Matches(existing, observed.Alias, observed.Options.Ratings, ModelServing.Of(observed.Options));
            if (existing?.Revision != input.Revision && !justPublished)
                throw new PeerFailure(409, "This requested model configuration changed. Refresh it before saving.");
            if (existing is null && current.Records.Models.Length >= PeerLimits.Models)
                throw new PeerFailure(409, "The network model record limit was reached.");
            var desired = new SharedModel(id, input.Owner, input.Deployment, Next(current, self.Id), alias!, input.Ratings!,
                Serving: input.Serving ?? existing?.Serving);
            if (existing is not null && Matches(existing, desired.Alias, desired.Ratings, desired.Serving))
                return current.Disk;
            return current.Disk with { Records = current.Records.Merge(NetworkRecords.Empty with
            {
                Identity = current.Records.Identity, Models = [desired]
            }) };
        }, cancellation);
        if (after.Self?.Id == input.Owner)
            await ApplyIntentsAsync(stopping.Token);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task EditLocalModelAsync(HttpRequest request, ModelUpdate input, CancellationToken cancellation)
    {
        var current = Snapshot;
        if (current.Records.Identity is null)
        {
            await providers.EditAsync(input, cancellation);
            return;
        }
        var self = RequireMember(current);
        RequireAdmin(current, AdminHeader(request));
        var model = providers.View.Models.FirstOrDefault(item => item.Id == input.Id);
        if (model is null)
            throw new PeerFailure(404, "The deployment was not found in the local inventory.");
        if (model.Options.Revision != input.Revision)
            throw new PeerFailure(409, "This model changed. Refresh the catalog before editing.");
        var existing = current.Records.Models.FirstOrDefault(item => item.Owner == self.Id && item.Deployment == input.Id);
        if (existing is not null && !Matches(existing, model.Alias, model.Options.Ratings, ModelServing.Of(model.Options)))
            throw new PeerFailure(409, "A shared model change is pending. Edit its requested configuration in Private network.");
        await SetModelIntentAsync(request, new(self.Id, input.Id, existing?.Revision, input.Alias, input.Ratings ?? [],
            new(input.ServedContext, input.EmbeddingDimensions,
                string.IsNullOrWhiteSpace(input.EmbeddingFamily) ? null : input.EmbeddingFamily.Trim(), input.MediaTokensPerItem)),
            cancellation, local: true);
    }

    public async Task EditRatingsAsync(HttpRequest request, string alias, Dictionary<string, int?> ratings, CancellationToken cancellation)
    {
        if (Snapshot.Records.Identity is null)
        {
            await providers.ApplyRatingsAsync(alias, ratings, cancellation);
            return;
        }
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            var models = current.Records.Models.Where(model => !model.Removed &&
                current.Nodes.TryGetValue(model.Owner, out var owner) && !owner.Removed &&
                string.Equals(model.Alias, alias, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (models.Length == 0)
                throw new PeerFailure(404, "No shared deployments have this alias.");
            var revision = Next(current, self.Id);
            var changed = models.Select(model =>
            {
                var merged = new Dictionary<string, int?>(model.Ratings, StringComparer.Ordinal);
                foreach (var (label, rating) in ratings)
                    merged[label] = rating;
                if (!PeerValidation.ValidModelSettings(model.Alias, merged, model.Serving))
                    throw new PeerFailure(400, "Use capability labels and ratings from zero to five, or leave a rating blank.");
                return model with { Ratings = merged, Revision = revision };
            }).ToArray();
            return current.Disk with { Records = current.Records.Merge(NetworkRecords.Empty with
            {
                Identity = current.Records.Identity, Models = changed
            }) };
        }, cancellation);
        await ApplyIntentsAsync(stopping.Token);
        await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task<PushResult> SetProviderIntentAsync(HttpRequest request, SharedProviderUpdate input, CancellationToken cancellation)
    {
        if (input.Owner == Guid.Empty || input.Id == Guid.Empty || !Enum.IsDefined(input.Profile) ||
            input.Id is null && !input.Enabled)
            throw new PeerFailure(400, "Select an enrolled host and a valid provider profile.");
        var address = EndpointAddress(input.Profile, input.Address);
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            if (!current.Nodes.TryGetValue(input.Owner, out var owner) || owner.Removed)
                throw new PeerFailure(404, "Select an enrolled provider host.");
            var existing = ProviderIntent(current, input.Owner, input.Id);
            if (input.Id is not null && existing is null)
                throw new PeerFailure(404, "The provider registration was not found.");
            if (existing?.Revision != input.Revision)
                throw new PeerFailure(409, "This requested provider configuration changed. Refresh it before saving.");
            return WithProviderIntent(current, self.Id, input.Owner, existing?.Id ?? Guid.NewGuid(), input.Profile, address, input.Enabled);
        }, cancellation);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task<EndpointStatus> ConfigureLocalProviderAsync(HttpRequest request, EndpointUpdate update, CancellationToken cancellation)
    {
        await intentGate.WaitAsync(cancellation);
        try
        {
            var current = Snapshot;
            if (current.Records.Identity is null)
                return await providers.ConfigureAsync(update, cancellation);
            var address = EndpointAddress(update.Profile, update.Address);
            var previous = providers.View.Endpoints.FirstOrDefault(item => item.Id == update.Id);
            var localOnly = previous is not null && previous.Address == address && previous.Profile == update.Profile;
            if (localOnly)
                return await providers.ConfigureAsync(update, cancellation);
            var self = RequireMember(current);
            var desired = ProviderIntent(current, self.Id, update.Id);
            var approve = desired is { Enabled: true } && desired.Profile == update.Profile && desired.Address == address;
            if (!approve)
            {
                RequireAdmin(current, AdminHeader(request));
                ValidateProviderRoom(current, self.Id, update.Id, address, true);
            }
            if (previous is null && desired is not null && !approve)
                throw new PeerFailure(409, "Confirm the requested address and profile from the owning host's provider form.");
            var applied = await providers.ConfigureAsync(previous is null && approve ? update with { Id = null } : update,
                cancellation, approve ? desired?.Id : null);
            if (!approve)
            {
                var (before, after) = await ChangeAsync(state =>
                {
                    var owner = RequireMember(state);
                    RequireAdmin(state, AdminHeader(request));
                    return WithProviderIntent(state, owner.Id, owner.Id, applied.Id, applied.Profile, applied.Address, true);
                }, cancellation);
                await PushChangesAsync(before, after, stopping.Token);
            }
            return applied;
        }
        finally { intentGate.Release(); }
    }

    public async Task RemoveLocalProviderAsync(HttpRequest request, Guid id, CancellationToken cancellation)
    {
        await intentGate.WaitAsync(cancellation);
        try
        {
            var current = Snapshot;
            if (current.Records.Identity is null)
            {
                await providers.RemoveAsync(id, cancellation);
                return;
            }
            var self = RequireMember(current);
            var applied = providers.View.Endpoints.FirstOrDefault(item => item.Id == id)
                ?? throw new PeerFailure(404, "The local provider registration was not found.");
            var desired = ProviderIntent(current, self.Id, id);
            if (desired is not { Enabled: false })
                RequireAdmin(current, AdminHeader(request));
            await providers.RemoveAsync(id, cancellation);
            if (desired is not { Enabled: false })
            {
                var (before, after) = await ChangeAsync(state =>
                {
                    var owner = RequireMember(state);
                    RequireAdmin(state, AdminHeader(request));
                    return WithProviderIntent(state, owner.Id, owner.Id, id, applied.Profile, applied.Address, false);
                }, cancellation);
                await PushChangesAsync(before, after, stopping.Token);
            }
        }
        finally { intentGate.Release(); }
    }

    private static SharedProvider? ProviderIntent(PeerSnapshot current, Guid owner, Guid? id) => id is null ? null :
        current.Records.Providers?.FirstOrDefault(item => item.Owner == owner && item.Id == id);

    private static string EndpointAddress(ProviderProfile profile, string? value)
    {
        try { return ProviderCatalog.NormalizeAddress(string.IsNullOrWhiteSpace(value) ? ProviderCatalog.DefaultAddress(profile) : value); }
        catch (ProviderFailure error) { throw new PeerFailure(error.Status, error.Message); }
    }

    private PeerDisk WithProviderIntent(PeerSnapshot current, Guid writer, Guid owner, Guid id,
        ProviderProfile profile, string address, bool enabled)
    {
        var existing = ProviderIntent(current, owner, id);
        if (existing is not null && existing.Profile == profile && existing.Address == address && existing.Enabled == enabled)
            return current.Disk;
        ValidateProviderRoom(current, owner, id, address, enabled);
        return current.Disk with { Records = current.Records.Merge(NetworkRecords.Empty with
        {
            Identity = current.Records.Identity,
            Providers = [new(id, owner, Next(current, writer), profile, address, enabled)]
        }) };
    }

    private static void ValidateProviderRoom(PeerSnapshot current, Guid owner, Guid? id, string address, bool enabled)
    {
        var desired = current.Records.Providers ?? [];
        if (desired.Length >= PeerLimits.Providers && ProviderIntent(current, owner, id) is null)
            throw new PeerFailure(409, "The network provider registration limit was reached.");
        var active = desired.Where(item => item.Owner == owner && item.Id != id && item.Enabled).ToArray();
        if (enabled && (active.Length >= ProviderLimits.MaxEndpoints || active.Any(item => item.Address == address)))
            throw new PeerFailure(409, "This address is already requested on the host or its registration limit was reached.");
    }

    private static bool Matches(SharedModel desired, string alias, Dictionary<string, int?> ratings, ModelServing? serving) =>
        desired.Alias == alias && (desired.Serving is null || desired.Serving == serving) &&
        desired.Ratings.Count == ratings.Count && desired.Ratings.All(item => ratings.TryGetValue(item.Key, out var value) && value == item.Value);

    private async Task<(Dictionary<Guid, IntentReport> Endpoints, Dictionary<string, IntentReport> Models)> ApplyIntentsAsync(
        CancellationToken cancellation)
    {
        await intentGate.WaitAsync(cancellation);
        try
        {
            intentWaitingForProvider = false;
            var current = Snapshot;
            Dictionary<Guid, IntentReport> endpoints = [];
            Dictionary<string, IntentReport> models = [];
            if (current.Self is not { } self || DeviceChanged(current))
                return (endpoints, models);
            foreach (var desired in (current.Records.Providers ?? []).Where(item => item.Owner == self.Id))
            {
                var effective = providers.View.Endpoints.FirstOrDefault(item => item.Id == desired.Id);
                try
                {
                    if (!desired.Enabled && effective is not null)
                        await providers.RemoveAsync(effective.Id, cancellation, automatic: true);
                    if (desired.Enabled && (effective is null || effective.Profile != desired.Profile || effective.Address != desired.Address))
                        effective = await providers.ConfigureAsync(new(effective?.Id, desired.Profile, ProviderMode.Attached, desired.Address,
                            null, false, null, null), cancellation, desired.Id, automatic: true);
                    endpoints[desired.Id] = effective?.CredentialNeeded == true && desired.Enabled
                        ? new(desired.Revision, IntentState.LocalAction, "Enter the provider credential on the owning host.")
                        : new(desired.Revision, IntentState.Applied);
                }
                catch (ProviderFailure error) { endpoints[desired.Id] = Report(desired.Revision, error); }
            }
            foreach (var desired in current.Records.Models.Where(item => item.Owner == self.Id && !item.Removed))
            {
                var model = providers.View.Models.FirstOrDefault(item => item.Id == desired.Deployment);
                if (model is null)
                    continue;
                try
                {
                    var serving = desired.Serving ?? ModelServing.Of(model.Options);
                    if (!Matches(desired, model.Alias, model.Options.Ratings, ModelServing.Of(model.Options)))
                        await providers.EditAsync(new(model.Id, desired.Alias, desired.Ratings, serving.ServedContext,
                            serving.EmbeddingDimensions, serving.EmbeddingFamily, serving.MediaTokensPerItem, model.Options.Revision),
                            cancellation, automatic: true);
                    models[desired.Deployment] = new(desired.Revision, IntentState.Applied);
                }
                catch (ProviderFailure error) { models[desired.Deployment] = Report(desired.Revision, error); }
            }
            intentWaitingForProvider = endpoints.Values.Concat(models.Values).Any(report => report.State == IntentState.Pending);
            return (endpoints, models);
        }
        finally { intentGate.Release(); }
    }

    private static IntentReport Report(Revision revision, ProviderFailure error) => new(revision, error.Status switch
    {
        412 => IntentState.LocalAction,
        423 => IntentState.Pending,
        _ => IntentState.Rejected
    }, error.Message);
}
