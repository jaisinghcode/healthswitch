using HealthSwitch.Data;
using HealthSwitch.Domain;
using HealthSwitch.Gateway;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Sim;

/// <summary>
/// Stands in for an API-connected payer's own claims system (Sapphire HMO in the demo).
/// It accepts a message, then answers a moment later through the gateway, exactly as a real payer
/// would call our on_* endpoints. Requests under the automatic limit are decided by its rules; larger
/// ones wait for a reviewer on Sapphire's own website, whose decision also goes back through the gateway.
/// Toggle it offline in the switch console to see retries.
/// </summary>
public class PayerSimulator(IServiceScopeFactory scopes, ILogger<PayerSimulator> log)
{
    public static readonly TimeSpan ThinkTime = TimeSpan.FromSeconds(1.5);

    public void Schedule(string payerCode, string api, string senderCode, Guid? claimId, Guid correlationId, Guid workflowId, string payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ThinkTime);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var gw = scope.ServiceProvider.GetRequiredService<GatewayService>();
                await Answer(db, gw, payerCode, api, senderCode, claimId, correlationId, workflowId, payload);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Simulated payer {Payer} failed to answer {Api}", payerCode, api);
            }
        });
    }

    static async Task Answer(AppDb db, GatewayService gw, string payer, string api, string hospital, Guid? claimId, Guid corr, Guid wf, string payload)
    {
        var payerName = (await db.Organisations.FindAsync(payer))?.Name ?? payer;
        switch (api)
        {
            case "coverageeligibility.check":
            {
                var q = Fhir.Read<EligibilityQuery>(payload);
                var m = q is null ? null : await db.Members.FindAsync(q.MemberNo);
                var a = new EligibilityAnswer(q?.MemberNo ?? "", m is { Active: true } && m.PayerCode == payer, $"Answered by {payerName}'s system");
                await gw.AcceptAsync(new AcceptRequest("coverageeligibility.on_check", payer, hospital, Fhir.Json(a), CorrelationId: corr, WorkflowId: wf));
                break;
            }
            case "preauth.submit":
            case "communication.on_request":
            {
                var claim = claimId is null ? null : await db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == claimId);
                if (claim is null) return;
                var total = claim.Items.Sum(i => i.Requested);
                // Sapphire's own rules: small requests are decided at once; large ones, and any documents sent back
                // after a reviewer's query, go to a medical reviewer on Sapphire's own website.
                if (api == "communication.on_request" || total >= Seeder.SapphireAutoLimit)
                {
                    claim.AwaitingPayer = true;
                    claim.Log(api == "communication.on_request"
                        ? $"{payerName} system: documents received, back with the medical reviewer"
                        : $"{payerName} rules engine: ₦{total:N0} is over the ₦{Seeder.SapphireAutoLimit:N0} automatic limit; referred to a medical reviewer");
                    await db.SaveChangesAsync();
                    return;
                }
                var tariffs = await db.Services.ToDictionaryAsync(s => s.Code, s => s.Tariff);
                var decisions = claim.Items.OrderBy(i => i.Seq).Select(i =>
                {
                    if (i.Status is ItemStatus.Approved or ItemStatus.Declined)
                        return new DecisionDto(i.Id, i.Status, i.Approved, i.Reason, i.DocType, i.Note);
                    var (status, approved, reason) = ClaimRules.TariffRule(i.Requested, tariffs.GetValueOrDefault(i.Code, i.Requested));
                    return new DecisionDto(i.Id, status, approved, reason, "", "");
                }).ToList();
                var remark = new RemarkDto($"{payerName} · automated rules", "Decided automatically: each service approved up to the agreed tariff.");
                await gw.AcceptAsync(new AcceptRequest("preauth.on_submit", payer, hospital, Fhir.Json(new DecisionPayload(decisions, remark)),
                    CorrelationId: corr, WorkflowId: wf, ClaimId: claimId));
                break;
            }
            case "claim.submit":
                await gw.AcceptAsync(new AcceptRequest("claim.on_submit", payer, hospital, Fhir.Json(new OutcomePayload("approved")),
                    CorrelationId: corr, WorkflowId: wf, ClaimId: claimId));
                break;
        }
    }
}
