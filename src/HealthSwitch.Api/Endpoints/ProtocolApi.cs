using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HealthSwitch.Data;
using HealthSwitch.Gateway;
using HealthSwitch.Sim;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Endpoints;

/// <summary>
/// HCX-style exchange endpoints: POST /v1/{resource}/{action} with x-hcx-* headers.
/// Every call returns 202 Accepted at once; the answer arrives later as the matching on_* call.
/// </summary>
public static class ProtocolApi
{
    static IResult Error(string code, string message, int status = 400) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/{resource}/{action}", async (string resource, string action, HttpRequest req, GatewayService gw, CancellationToken ct) =>
        {
            string H(string name) => req.Headers[name].ToString();
            var sender = H("x-hcx-sender_code");
            var recipient = H("x-hcx-recipient_code");
            if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(recipient))
                return Error("ERR_MANDATORY_HEADER_MISSING", "x-hcx-sender_code and x-hcx-recipient_code are required.");
            if (!Guid.TryParse(H("x-hcx-api_call_id"), out var callId))
                return Error("ERR_INVALID_API_CALL_ID", "x-hcx-api_call_id must be a UUID.");
            Guid? corr = null, wf = null, claimId = null;
            if (!string.IsNullOrEmpty(H("x-hcx-correlation_id")))
            {
                if (!Guid.TryParse(H("x-hcx-correlation_id"), out var c)) return Error("ERR_INVALID_CORRELATION_ID", "x-hcx-correlation_id must be a UUID.");
                corr = c;
            }
            if (!string.IsNullOrEmpty(H("x-hcx-workflow_id")))
            {
                if (!Guid.TryParse(H("x-hcx-workflow_id"), out var w)) return Error("ERR_INVALID_WORKFLOW_ID", "x-hcx-workflow_id must be a UUID.");
                wf = w;
            }
            if (Guid.TryParse(H("x-hs-claim_id"), out var cl)) claimId = cl;

            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync(ct);
            try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
            catch (JsonException) { return Error("ERR_INVALID_PAYLOAD", "The body is not valid JSON."); }

            try
            {
                var (env, duplicate) = await gw.AcceptAsync(new AcceptRequest($"{resource}.{action}", sender, recipient, body, callId, corr, wf, claimId), ct);
                return Results.Json(new
                {
                    api_call_id = env.ApiCallId, correlation_id = env.CorrelationId, workflow_id = env.WorkflowId,
                    message_no = env.MessageNo, status = env.Status, duplicate, timestamp = DateTimeOffset.UtcNow,
                }, statusCode: 202);
            }
            catch (GatewayException ex) { return Error(ex.Code, ex.Message, ex.Status); }
        });

        // Full delivery timeline of one conversation.
        app.MapGet("/v1/requests/{correlationId:guid}", async (Guid correlationId, AppDb db) =>
            Results.Ok(await db.Envelopes.AsNoTracking().Where(e => e.CorrelationId == correlationId).OrderBy(e => e.CreatedAt)
                .Select(e => new { e.MessageNo, e.Api, e.SenderCode, e.RecipientCode, e.Status, e.Attempts, e.CreatedAt, e.UpdatedAt }).ToListAsync()));

        // Payment processor webhook: signature checked with HMAC-SHA256 before anything is trusted.
        app.MapPost("/v1/webhooks/processor", async (HttpRequest req, AppDb db, GatewayService gw, IConfiguration config, CancellationToken ct) =>
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync(ct);
            if (!Signature.Valid(Signature.Secret(config), body, req.Headers["X-Signature"].ToString()))
                return Error("ERR_ACCESS_DENIED", "Invalid webhook signature.", 401);
            var p = JsonSerializer.Deserialize<ProcessorEvent>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p is null) return Error("ERR_INVALID_PAYLOAD", "Missing payment details.");
            var batch = await db.Batches.FirstOrDefaultAsync(b => b.VirtualAccount == p.Account, ct);
            if (batch is null) return Error("ERR_UNKNOWN_ACCOUNT", "No batch uses this account.", 404);
            if (p.Amount != batch.Total)
            {
                batch.Status = "exception"; await db.SaveChangesAsync(ct);
                return Results.Accepted(value: new { status = "exception", reason = "Amount does not match the invoice; nothing settled." });
            }
            await gw.AcceptAsync(new AcceptRequest("payment.webhook", Seeder.Processor, Seeder.SwitchCode, Fhir.Json(new BatchPayload(batch.BatchNo, p.Amount))), ct);
            return Results.Accepted(value: new { status = "accepted" });
        });
    }

    public record ProcessorEvent(string Account, long Amount, string Reference);
}

public static class Signature
{
    public static string Secret(IConfiguration config) => config["PROCESSOR_WEBHOOK_SECRET"] is { Length: > 0 } s ? s : "local-dev-secret";

    public static string Sign(string secret, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static bool Valid(string secret, string body, string signature) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, body)), Encoding.UTF8.GetBytes(signature ?? ""));
}

public static class SimEndpoints
{
    public static void Map(WebApplication app)
    {
        // The simulated API payer's own endpoint. Returns 503 while it is switched off.
        app.MapPost("/sim/{code}/{api}", async (string code, string api, HttpRequest req, AppDb db, PayerSimulator sim, CancellationToken ct) =>
        {
            var org = await db.Organisations.FindAsync(new object[] { code }, ct);
            if (org is null) return Results.NotFound();
            if (!org.Online) return Results.StatusCode(503);
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync(ct);
            Guid? claimId = Guid.TryParse(req.Headers["x-hs-claim_id"].ToString(), out var c) ? c : null;
            Guid.TryParse(req.Headers["x-hcx-correlation_id"].ToString(), out var corr);
            Guid.TryParse(req.Headers["x-hcx-workflow_id"].ToString(), out var wf);
            sim.Schedule(code, api, req.Headers["x-hcx-sender_code"].ToString(), claimId, corr, wf, body);
            return Results.Accepted();
        });
    }
}
