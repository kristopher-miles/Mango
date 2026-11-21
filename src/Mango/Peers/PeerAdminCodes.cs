using System.Security.Cryptography;

namespace Mango.Peers;

internal sealed record LoginCodeClaim(string Digest, Guid Attempt, Revision Authority);
internal sealed record LoginCodeLease(Guid Id, DateTimeOffset ExpiresAt);
internal sealed record LoginCodeReference(Guid Issuer, Guid Id, DateTimeOffset ExpiresAt);
internal sealed record LoginCodeCheck(Guid Id, Revision Authority);
internal sealed record LoginCodeStatus(bool Active);

internal sealed partial class PeerNetwork
{
    internal static byte[] LoginCodeIssuer(Guid node) => SHA256.HashData(node.ToByteArray())[..4];

    internal IssuedAdminCode IssueBrowserLoginCode(HttpRequest request)
    {
        var current = Snapshot;
        RequireAdmin(current, AdminHeader(request));
        return Administration!.IssueCode(current, new());
    }

    internal async Task<LoginCodeReference?> ClaimLoginCodeAsync(PeerSnapshot current, string digest, string? issuerTag,
        CancellationToken cancellation)
    {
        if (current.Self is not { } self || current.Records.Authority is not { } authority)
            return null;
        var request = new LoginCodeClaim(digest, Guid.NewGuid(), authority.Revision);
        if (issuerTag is null || Convert.ToHexString(LoginCodeIssuer(self.Id)) == issuerTag)
        {
            var local = Administration!.Claim(current, request, self.Id);
            if (local is not null)
                return new(self.Id, local.Id, local.ExpiresAt);
            if (issuerTag is null)
                return null;
        }

        foreach (var peer in current.Others.Where(peer => Convert.ToHexString(LoginCodeIssuer(peer.Id)) == issuerTag))
            if ((await SendAsync<LoginCodeLease>(current, peer, "login-code/claim", request, cancellation))?.Body is { } lease)
                return new(peer.Id, lease.Id, lease.ExpiresAt);
        return null;
    }

    internal async Task<bool> LoginCodeActiveAsync(LoginCodeReference reference, CancellationToken cancellation)
    {
        var current = Snapshot;
        if (current.Self is not { } self || current.Records.Authority is not { } authority)
            return false;
        var check = new LoginCodeCheck(reference.Id, authority.Revision);
        if (self.Id == reference.Issuer)
            return Administration!.Check(current, check, self.Id).Active;
        return current.Nodes.TryGetValue(reference.Issuer, out var issuer) && !issuer.Removed &&
            (await SendAsync<LoginCodeStatus>(current, issuer, "login-code/check", check, cancellation))?.Body?.Active == true;
    }
}
