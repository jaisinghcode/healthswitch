using HealthSwitch.Data;
using HealthSwitch.Domain;
using HealthSwitch.Gateway;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Messaging;

/// <summary>What happens when a message reaches its recipient: inbox updates, decisions, ledger and payment.</summary>
public class DeliveryEffects(AppDb db, IServiceProvider services, ILogger<DeliveryEffects> log)
{
    GatewayService Gateway => services.GetRequiredService<GatewayService>();

    public async Task OnDeliveredAsync(MessageEnvelope env, CancellationToken ct)
    {
        switch (env.Api)
        {
            case "coverageeligibility.check": await EligibilityCheckDelivered(env, ct); break;
            case "coverageeligibility.on_check": await EligibilityAnswered(env, ct); break;
            case "preauth.submit":
            case "claim.submit":
            case "communication.on_request": await RequestReachedPayer(env, ct); break;
            case "preauth.on_submit":
            case "communication.request": await DecisionsReachedHospital(env, ct); break;
            case "claim.on_submit": await FinalDecisionReachedHospital(env, ct); break;
            case "payment.webhook": await PaymentConfirmed(env, ct); break;
            case "paymentnotice.request": await PaymentNoticeReachedHospital(env, ct); break;
            default: log.LogInformation("{Api} delivered; no follow-up needed", env.Api); break;
        }
    }

    async Task<Claim?> LoadClaim(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Claims.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);

    async Task<string> NameOf(string code, CancellationToken ct) =>
        (await db.Organisations.FindAsync(new object[] { code }, ct))?.Name ?? code;

    async Task EligibilityCheckDelivered(MessageEnvelope env, CancellationToken ct)
    {
        var payer = await db.Organisations.FindAsync(new object[] { env.RecipientCode }, ct);
        if (payer?.Mode == ConnectionMode.Api) return; // the payer's own system answers with on_check
        // Portal payers upload a member list; the switch answers on their behalf.
        var q = Fhir.Read<EligibilityQuery>(env.Payload);
        var m = q is null ? null : await db.Members.FindAsync(new object[] { q.MemberNo }, ct);
        var answer = new EligibilityAnswer(q?.MemberNo ?? "", m is { Active: true }, $"Answered from {payer?.Name}'s uploaded member list");
        await Gateway.AcceptAsync(new AcceptRequest("coverageeligibility.on_check", Seeder.SwitchCode, env.SenderCode, Fhir.Json(answer),
            CorrelationId: env.CorrelationId, WorkflowId: env.WorkflowId), ct);
    }

    async Task EligibilityAnswered(MessageEnvelope env, CancellationToken ct)
    {
        var a = Fhir.Read<EligibilityAnswer>(env.Payload);
        var check = await db.EligibilityChecks.FindAsync(new object[] { env.CorrelationId }, ct);
        if (check is null || a is null) return;
        check.Status = a.Active ? "active" : "inactive"; check.Via = a.Via; check.At = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    async Task RequestReachedPayer(MessageEnvelope env, CancellationToken ct)
    {
        var claim = await LoadClaim(env.ClaimId, ct);
        if (claim is null) return;
        var payer = await db.Organisations.FindAsync(new object[] { env.RecipientCode }, ct);
        claim.Log($"Delivered to {payer?.Name}");
        if (payer?.Mode == ConnectionMode.Portal)
        {
            claim.AwaitingPayer = true;
            claim.Log($"Placed in {payer.Name} review inbox");
        }
        await db.SaveChangesAsync(ct);
    }

    async Task DecisionsReachedHospital(MessageEnvelope env, CancellationToken ct)
    {
        var claim = await LoadClaim(env.ClaimId, ct);
        var p = Fhir.Read<DecisionPayload>(env.Payload);
        if (claim is null || p is null) return;
        foreach (var d in p.Decisions)
        {
            var item = claim.Items.FirstOrDefault(i => i.Id == d.LineId);
            if (item is null) continue;
            item.Status = d.Status; item.Approved = d.Approved; item.Reason = d.Reason ?? "";
            item.DocType = d.DocType ?? ""; item.Note = d.Note ?? "";
        }
        if (p.Remark is { } r && !string.IsNullOrWhiteSpace(r.Text))
        {
            db.Remarks.Add(new Remark { ClaimId = claim.Id, By = r.By, Text = r.Text });
            claim.Log("Reviewer remark: " + r.Text);
        }
        claim.DecidedAt = DateTimeOffset.UtcNow;
        claim.Log("Decision received: " + ClaimRules.HospitalStatus(claim, null).Label);
        await db.SaveChangesAsync(ct);
    }

    async Task FinalDecisionReachedHospital(MessageEnvelope env, CancellationToken ct)
    {
        var claim = await LoadClaim(env.ClaimId, ct);
        var p = Fhir.Read<OutcomePayload>(env.Payload);
        if (claim is null || p is null) return;
        if (p.Outcome == "approved")
        {
            claim.Stage = Stages.Final;
            var amount = ClaimRules.ApprovedTotal(claim.Items);
            if (amount > 0 && !await db.Ledger.AnyAsync(l => l.ClaimId == claim.Id, ct))
                db.Ledger.Add(new LedgerEntry
                {
                    ClaimId = claim.Id, AuthNo = claim.AuthNo, HospitalCode = claim.HospitalCode, PayerCode = claim.PayerCode,
                    PatientName = claim.PatientName, Amount = amount,
                });
            claim.Log($"Final claim approved; ledger entry ₦{amount:N0}");
        }
        else
        {
            claim.Stage = Stages.ClaimDeclined;
            claim.Log("Final claim declined");
        }
        await db.SaveChangesAsync(ct);
    }

    async Task PaymentConfirmed(MessageEnvelope env, CancellationToken ct)
    {
        var p = Fhir.Read<BatchPayload>(env.Payload);
        var batch = p is null ? null : await db.Batches.FirstOrDefaultAsync(b => b.BatchNo == p.BatchNo, ct);
        if (batch is null) return;
        await Gateway.AcceptAsync(new AcceptRequest("paymentnotice.request", batch.PayerCode, batch.HospitalCode,
            Fhir.Json(new BatchPayload(batch.BatchNo, batch.Total))), ct);
    }

    async Task PaymentNoticeReachedHospital(MessageEnvelope env, CancellationToken ct)
    {
        var p = Fhir.Read<BatchPayload>(env.Payload);
        var batch = p is null ? null : await db.Batches.FirstOrDefaultAsync(b => b.BatchNo == p.BatchNo, ct);
        if (batch is null) return;
        batch.Status = "paid"; batch.PaidAt = DateTimeOffset.UtcNow;
        var entries = await db.Ledger.Where(l => l.BatchNo == batch.BatchNo).ToListAsync(ct);
        foreach (var l in entries) { l.Status = "paid"; l.PaidAt = batch.PaidAt; }
        var ids = entries.Select(e => e.ClaimId).ToList();
        foreach (var c in await db.Claims.Where(c => ids.Contains(c.Id)).ToListAsync(ct))
            c.Log($"Paid in {batch.BatchNo} by {await NameOf(batch.PayerCode, ct)}");
        await db.SaveChangesAsync(ct);
    }
}
