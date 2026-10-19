using System.Net.Http.Headers;
using System.Text;
using HealthSwitch.Data;
using HealthSwitch.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Messaging;

/// <summary>q.route: picks the recipient's connection type and puts the message on the right delivery queue.</summary>
public class RouterConsumer(AppDb db, ILogger<RouterConsumer> log) : IConsumer<RouteMessage>
{
    public async Task Consume(ConsumeContext<RouteMessage> context)
    {
        var env = await db.Envelopes.FindAsync(new object[] { context.Message.EnvelopeId }, context.CancellationToken);
        if (env is null) { log.LogWarning("Envelope {Id} not found", context.Message.EnvelopeId); return; }
        var recipient = await db.Organisations.FindAsync(new object[] { env.RecipientCode }, context.CancellationToken);
        var queue = recipient?.Mode == ConnectionMode.Api ? Queues.DeliverApi : Queues.DeliverPortal;
        env.Status = "dispatched"; env.Stage = queue; env.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
        var endpoint = await context.GetSendEndpoint(new Uri("queue:" + queue));
        await endpoint.Send(new DeliverMessage(env.Id), context.CancellationToken);
    }
}

public class PayerUnavailableException(string message) : Exception(message);

/// <summary>q.deliver.api: sends the message to the payer's own system. Failures are retried, then dead-lettered.</summary>
public class ApiAdapterConsumer(AppDb db, IHttpClientFactory http, IConfiguration config, DeliveryEffects effects, ILogger<ApiAdapterConsumer> log) : IConsumer<DeliverMessage>
{
    public async Task Consume(ConsumeContext<DeliverMessage> context)
    {
        var ct = context.CancellationToken;
        var env = await db.Envelopes.FindAsync(new object[] { context.Message.EnvelopeId }, ct);
        if (env is null || env.Status is "delivered" or "dead") return;

        env.Attempts++; env.Stage = Queues.DeliverApi; env.UpdatedAt = DateTimeOffset.UtcNow;
        string? error = null;
        try
        {
            // In the MVP the API payer is simulated inside this app at /sim/{code}/{api}.
            // A real payer would register its own endpoint URL in the participant registry.
            var client = http.CreateClient("payer");
            var req = new HttpRequestMessage(HttpMethod.Post, $"{config["SelfBaseUrl"]}/sim/{env.RecipientCode}/{env.Api}")
            {
                Content = new StringContent(env.Payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-hcx-sender_code", env.SenderCode);
            req.Headers.Add("x-hcx-recipient_code", env.RecipientCode);
            req.Headers.Add("x-hcx-api_call_id", env.ApiCallId.ToString());
            req.Headers.Add("x-hcx-correlation_id", env.CorrelationId.ToString());
            req.Headers.Add("x-hcx-workflow_id", env.WorkflowId.ToString());
            if (env.ClaimId is { } cid) req.Headers.Add("x-hs-claim_id", cid.ToString());
            var key = config["DEMO_ACCESS_KEY"];
            if (!string.IsNullOrEmpty(key)) req.Headers.Add("X-Demo-Key", key);
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) error = $"Payer answered {(int)res.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error = "Payer not reachable: " + ex.Message;
        }

        if (error is not null)
        {
            env.Status = "retrying"; env.Stage = "q.retry"; env.Error = error;
            await SetClaimDelivery(env, "retrying", ct);
            await db.SaveChangesAsync(ct);
            log.LogWarning("{MessageNo} attempt {Attempt} failed: {Error}", env.MessageNo, env.Attempts, error);
            throw new PayerUnavailableException(error); // MassTransit retries, then faults to q.dead
        }

        env.Status = "delivered"; env.Stage = "done"; env.Error = null;
        await SetClaimDelivery(env, "delivered", ct);
        await db.SaveChangesAsync(ct);
        await effects.OnDeliveredAsync(env, ct);
    }

    async Task SetClaimDelivery(MessageEnvelope env, string state, CancellationToken ct)
    {
        if (!env.Track || env.ClaimId is null) return;
        var claim = await db.Claims.FindAsync(new object[] { env.ClaimId.Value }, ct);
        if (claim is not null) claim.Delivery = state;
    }
}

/// <summary>q.deliver.portal: places the message in the recipient's portal inbox.</summary>
public class PortalAdapterConsumer(AppDb db, DeliveryEffects effects) : IConsumer<DeliverMessage>
{
    public async Task Consume(ConsumeContext<DeliverMessage> context)
    {
        var ct = context.CancellationToken;
        var env = await db.Envelopes.FindAsync(new object[] { context.Message.EnvelopeId }, ct);
        if (env is null || env.Status is "delivered" or "dead") return;
        env.Attempts++; env.Status = "delivered"; env.Stage = "done"; env.UpdatedAt = DateTimeOffset.UtcNow;
        if (env.Track && env.ClaimId is { } cid)
        {
            var claim = await db.Claims.FindAsync(new object[] { cid }, ct);
            if (claim is not null) claim.Delivery = "delivered";
        }
        await db.SaveChangesAsync(ct);
        await effects.OnDeliveredAsync(env, ct);
    }
}

/// <summary>q.dead: a delivery failed every retry. Record it, tell the sender, wait for a replay.</summary>
public class DeadLetterConsumer(AppDb db, ILogger<DeadLetterConsumer> log) : IConsumer<Fault<DeliverMessage>>
{
    public async Task Consume(ConsumeContext<Fault<DeliverMessage>> context)
    {
        var ct = context.CancellationToken;
        var env = await db.Envelopes.FindAsync(new object[] { context.Message.Message.EnvelopeId }, ct);
        if (env is null || env.Status == "delivered") return;
        env.Status = "dead"; env.Stage = Queues.Dead; env.UpdatedAt = DateTimeOffset.UtcNow;
        if (env.ClaimId is { } cid)
        {
            var claim = await db.Claims.Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (claim is not null)
            {
                if (env.Track) claim.Delivery = "dead";
                claim.Log($"{env.Api} not delivered after {env.Attempts} attempts; sender notified, operations can replay it");
            }
        }
        await db.SaveChangesAsync(ct);
        log.LogError("{MessageNo} moved to dead letters: {Error}", env.MessageNo, env.Error);
    }
}
