using HealthSwitch.Data;
using HealthSwitch.Domain;
using HealthSwitch.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Gateway;

public class GatewayException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public record AcceptRequest(string Api, string Sender, string Recipient, string Payload, Guid? ApiCallId = null, Guid? CorrelationId = null, Guid? WorkflowId = null, Guid? ClaimId = null, bool Track = false);

/// <summary>
/// The front door. Runs the quick checks, stores the envelope, puts it on q.route, and returns at once
/// (202 Accepted). Everything slow happens in the queue consumers.
/// </summary>
public class GatewayService(AppDb db, ISendEndpointProvider send, ILogger<GatewayService> log)
{
    public static readonly HashSet<string> Apis = new()
    {
        "coverageeligibility.check", "coverageeligibility.on_check",
        "preauth.submit", "preauth.on_submit",
        "claim.submit", "claim.on_submit",
        "communication.request", "communication.on_request",
        "paymentnotice.request", "paymentnotice.on_request",
        "payment.webhook",
    };

    public async Task<(MessageEnvelope Envelope, bool Duplicate)> AcceptAsync(AcceptRequest r, CancellationToken ct = default)
    {
        if (!Apis.Contains(r.Api)) throw new GatewayException("ERR_INVALID_API", $"'{r.Api}' is not a supported API.", 404);
        var callId = r.ApiCallId ?? Guid.NewGuid();

        // Idempotency: the same api_call_id is processed once and returns the original message.
        var existing = await db.Envelopes.AsNoTracking().FirstOrDefaultAsync(e => e.ApiCallId == callId, ct);
        if (existing is not null) return (existing, true);

        var sender = await db.Organisations.FindAsync(new object[] { r.Sender }, ct)
                     ?? throw new GatewayException("ERR_INVALID_SENDER", $"Sender '{r.Sender}' is not registered.");
        var recipient = await db.Organisations.FindAsync(new object[] { r.Recipient }, ct)
                        ?? throw new GatewayException("ERR_INVALID_RECIPIENT", $"Recipient '{r.Recipient}' is not registered.");

        var no = await db.NextAsync("msg_seq", ct);
        var env = new MessageEnvelope
        {
            MessageNo = $"MSG-{no:D5}",
            Api = r.Api,
            SenderCode = sender.Code,
            RecipientCode = recipient.Code,
            ApiCallId = callId,
            CorrelationId = r.CorrelationId ?? Guid.NewGuid(),
            WorkflowId = r.WorkflowId ?? Guid.NewGuid(),
            ClaimId = r.ClaimId,
            Track = r.Track,
            Payload = string.IsNullOrWhiteSpace(r.Payload) ? "{}" : r.Payload,
            Status = "queued",
            Stage = "q.route",
        };
        db.Envelopes.Add(env);
        if (r.Track && r.ClaimId is { } cid)
        {
            var claim = await db.Claims.FindAsync(new object[] { cid }, ct);
            if (claim is not null) claim.Delivery = "queued";
        }
        await db.SaveChangesAsync(ct);

        var endpoint = await send.GetSendEndpoint(new Uri("queue:" + Queues.Route));
        await endpoint.Send(new RouteMessage(env.Id), ct);
        log.LogInformation("Accepted {MessageNo} {Api} {Sender} -> {Recipient}", env.MessageNo, env.Api, env.SenderCode, env.RecipientCode);
        return (env, false);
    }
}
