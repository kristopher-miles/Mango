using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mango.Providers;
using Mango.Scheduling;

namespace Mango.Peers;

internal sealed record PlacementCheck(Guid? Key, string Deployment, InferenceShape Shape);
internal sealed record PlacementCheckReply(bool Eligible);
internal sealed record AttemptControl(Guid Attempt, int? FenceMilliseconds, string ServingEpoch);
internal sealed record RouteAdvice(Guid Request, Guid Suggested);

internal sealed partial class PeerNetwork
{
    private const string AttemptHeader = "Mango-Attempt";
    private const string EpochHeader = "Mango-Placement";
    private const string AttemptErrorHeader = "Mango-Attempt-Result";
    private const int MaxQueueMilliseconds = QueuePolicy.MaxWaitSeconds * 1000;

    private sealed class TrackedSubmission : HttpContent
    {
        private readonly HttpContent body;
        private int writing;

        public TrackedSubmission(HttpContent body)
        {
            this.body = body;
            foreach (var header in body.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        public bool WritingStarted => Volatile.Read(ref writing) != 0;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Volatile.Write(ref writing, 1);
            return body.CopyToAsync(stream, context, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                body.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record ForwardTicket(Guid Request, Guid Attempt, Guid? Key, string Deployment,
        int AgeMilliseconds, int RemainingMilliseconds, string? Session, string ServingEpoch)
    {
        public bool Valid => Request != Guid.Empty && Attempt != Guid.Empty && Key != Guid.Empty &&
            Deployment is { Length: > 0 and <= 128 } && AgeMilliseconds >= 0 && RemainingMilliseconds > 0 &&
            (long)AgeMilliseconds + RemainingMilliseconds <= MaxQueueMilliseconds &&
            (Session is null || PeerCredentials.IsSecret(Session)) && Guid.TryParseExact(ServingEpoch, "N", out _);
    }

    public void MapAttemptRoutes(RouteGroupBuilder routes)
    {
        foreach (var operation in Enum.GetValues<InferenceOperation>())
            routes.MapPost($"/inference/{operation}", (HttpContext context) => ReceiveAttemptAsync(operation, context));
        routes.MapPost("/inference/eligible", (PlacementCheck input) => CheckPlacement(input));
        routes.MapPost("/inference/control", async (AttemptControl input, HttpContext context) =>
        {
            if (input.Attempt == Guid.Empty || input.FenceMilliseconds is < 0 or > MaxQueueMilliseconds)
                return Results.BadRequest();
            if (input.ServingEpoch != schedulingEpoch)
                return Results.Json(new AttemptReply(AttemptState.Uncertain, 502, "The serving Mango process changed."), Json);
            var fence = input.FenceMilliseconds is { } duration ? clock.GetUtcNow().AddMilliseconds(duration) : (DateTimeOffset?)null;
            var result = await serving.StatusAsync(AuthenticatedNode(context), input.Attempt, fence, context.RequestAborted);
            return Results.Json(result, Json);
        });
        routes.MapPost("/inference/advice", (RouteAdvice input, HttpContext context) =>
        {
            ReceiveAdvice(AuthenticatedNode(context), input);
            return Results.NoContent();
        });
    }

    private static Guid AuthenticatedNode(HttpContext context) => ((PeerAuthentication)context.Items[typeof(PeerAuthentication)]!).Node;

    private PlacementCheckReply CheckPlacement(PlacementCheck check)
    {
        if (!Ready || check.Shape is not { Valid: true } || check.Key == Guid.Empty || check.Deployment is not { Length: > 0 and <= 128 })
            return new(false);
        using var lease = AcquireLease(check.Key ?? Guid.Empty, null, check.Shape.Operation, out _, forwarded: true);
        return new(lease is not null && AllowsMedia(lease.Access, check.Shape) &&
            providers.Placement(check.Shape, check.Deployment, allowUnverifiedContext: false) is not null);
    }

    private static bool AllowsMedia(InferenceAccess access, InferenceShape shape) =>
        (!shape.Inputs.Contains("image") || access.Allows(InferenceScope.ImageInput)) &&
        (shape.Operation == InferenceOperation.Transcription || !shape.Inputs.Contains("audio") || access.Allows(InferenceScope.AudioInput));

    private async Task ReceiveAttemptAsync(InferenceOperation operation, HttpContext context)
    {
        var received = clock.GetUtcNow();
        var forward = Decode<ForwardTicket>(context.Request.Headers[AttemptHeader].ToString());
        if (forward is not { Valid: true })
        {
            await ReplyAsync(context, new(AttemptState.Rejected, 400, "The forwarded attempt metadata is invalid."));
            return;
        }
        if (!Ready)
        {
            await ReplyAsync(context, new(AttemptState.Rejected, 503, "The serving node is reconciling network authority."));
            return;
        }
        if (forward.ServingEpoch != schedulingEpoch)
        {
            await ReplyAsync(context, new(AttemptState.Rejected, 503, "The serving Mango process changed before submission."));
            return;
        }
        using var lease = AcquireLease(forward.Key ?? Guid.Empty, null, operation, out var status, forwarded: true);
        if (lease is null)
        {
            if (forward.Key is { } id && !Snapshot.Keys.ContainsKey(id))
                status = 503;
            await ReplyAsync(context, new(AttemptState.Rejected, status, "The serving node rejected the forwarded authority."));
            return;
        }
        context.Items[typeof(InferenceAccess)] = lease.Access;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token, lease.Access.Cancelled);
        var allowed = Math.Clamp(QueueWaitSeconds * 1000 - forward.AgeMilliseconds, 0, forward.RemainingMilliseconds);
        var ticket = new AttemptTicket(forward.Request, forward.Attempt, forward.Key, forward.Deployment,
            received.AddMilliseconds(-forward.AgeMilliseconds), received.AddMilliseconds(allowed), forward.Session, schedulingEpoch);
        context.Response.OnStarting(() =>
        {
            if (context.Items[typeof(ServingQueue)] is string epoch)
                context.Response.Headers[EpochHeader] = epoch;
            return Task.CompletedTask;
        });
        AttemptReply result;
        try
        {
            if (providers.Resources is { } resources)
                await resources.ClientArrivedAsync(cancellation.Token);
            using var payload = await providers.ReadInferenceAsync(operation, context, cancellation.Token);
            result = await SubmitLocalAsync(AuthenticatedNode(context), ticket, payload, lease.Access, context, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!context.RequestAborted.IsCancellationRequested)
                await ReplyAsync(context, new(AttemptState.Uncertain, 401, "Forwarded inference authority ended."));
            return;
        }
        catch (BadHttpRequestException error)
        {
            result = new(AttemptState.Rejected, error.StatusCode, "The forwarded inference body is invalid or too large.");
        }
        catch (ProviderFailure error)
        {
            result = new(AttemptState.Rejected, error.Status == 429 ? 503 : error.Status, error.Message);
        }
        if (result.State != AttemptState.Completed)
            await ReplyAsync(context, result);
    }

    private static async Task ReplyAsync(HttpContext context, AttemptReply result)
    {
        if (context.RequestAborted.IsCancellationRequested)
            return;
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.StatusCode = result.Status;
        context.Response.Headers[AttemptErrorHeader] = "1";
        await context.Response.WriteAsJsonAsync(result, Json, context.RequestAborted);
    }

    private async Task<AttemptReply> SubmitPeerAsync(Guid destination, AttemptTicket ticket, TimeSpan elapsed,
        TimeSpan remaining, InferenceRequest payload, HttpContext context, CancellationToken cancellation)
    {
        var current = Snapshot;
        if (!Ready || current.Self is not { } self || current.Records.Identity is not { } identity || current.Token is null ||
            DeviceChanged(current) || !current.Nodes.TryGetValue(destination, out var peer) || peer.Removed)
            return new(AttemptState.Rejected, 503, "The selected peer is no longer eligible.");
        var origin = Origins(peer).FirstOrDefault();
        if (origin is null || remaining.TotalMilliseconds < 1)
            return new(AttemptState.Rejected, 503, "The selected peer cannot be reached within the queue deadline.", Unreachable: origin is null);
        var metadata = new ForwardTicket(ticket.Request, ticket.Attempt, ticket.Key, ticket.Deployment,
            (int)elapsed.TotalMilliseconds, (int)remaining.TotalMilliseconds, ticket.Session, ticket.ServingEpoch);
        var body = new TrackedSubmission(payload.CreateBody());
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, $"{PeerRoot}inference/{payload.Operation}")) { Content = body };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
            request.Headers.Add(NodeHeader, self.Id.ToString("N"));
            request.Headers.Add(NetworkHeader, identity.Id.ToString("N"));
            request.Headers.Add(DigestHeader, current.Digest);
            request.Headers.Add(RevisionHeader, current.Latest.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(AttemptHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(metadata, Json)));
            Interlocked.Increment(ref submissions);
            using var response = await inferenceHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (Header(response, NodeHeader) != destination.ToString("N") || Header(response, NetworkHeader) != identity.Id.ToString("N"))
                return await StopPeerAsync(current, peer, ticket, remaining);
            var digest = Header(response, DigestHeader);
            contacts[peer.Id] = new(clock.GetUtcNow(), true, digest);
            QueueMismatch(peer.Id, digest);
            if (response.StatusCode == HttpStatusCode.Conflict && Header(response, AttemptErrorHeader) is null)
            {
                var notice = await response.Content.ReadFromJsonAsync<PeerNotice>(Json, cancellation);
                if (notice is not null)
                    await ApplyNoticeAsync(notice, cancellation);
                return new(AttemptState.Rejected, 503, "The selected peer rejected the network generation.");
            }
            if (!response.IsSuccessStatusCode)
            {
                if (Header(response, AttemptErrorHeader) == "1" &&
                    await response.Content.ReadFromJsonAsync<AttemptReply>(Json, cancellation) is { } failure)
                    return failure;
                return await StopPeerAsync(current, peer, ticket, remaining);
            }
            var target = context.Response;
            target.StatusCode = (int)response.StatusCode;
            target.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            target.Headers.CacheControl = "no-store";
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                await InferenceRequest.WriteOutputAsync(context, buffer.AsMemory(0, count), cancellation);
            }
            return new(AttemptState.Completed, 200, Epoch: Header(response, EpochHeader));
        }
        catch (Exception error) when (!body.WritingStarted && error is HttpRequestException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new(AttemptState.Rejected, 503, "The selected peer could not be reached.", Unreachable: true);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return await StopPeerAsync(current, peer, ticket, remaining);
        }
    }

    private async Task<AttemptReply> StopPeerAsync(PeerSnapshot current, PeerNode peer, AttemptTicket ticket,
        TimeSpan remaining)
    {
        Interlocked.Increment(ref stopQueries);
        try
        {
            // A fence covers the original submission's remaining duration even if the control arrives before its body.
            var fence = (int)Math.Clamp(remaining.TotalMilliseconds, 0, MaxQueueMilliseconds);
            var reply = await SendAsync<AttemptReply>(current, peer, "inference/control", new AttemptControl(ticket.Attempt, fence, ticket.ServingEpoch),
                stopping.Token, refresh: true, timeout: TimeSpan.FromSeconds(7));
            if (reply?.Body is { State: AttemptState.Rejected or AttemptState.Stopped or AttemptState.Failed } result)
                return result;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        return new(AttemptState.Uncertain, 502, "Mango could not confirm that the remote inference stopped.");
    }

    private static T? Decode<T>(string value) where T : class
    {
        if (value.Length is 0 or > 4096)
            return null;
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value), Json); }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }
}
