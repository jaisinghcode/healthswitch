using HealthSwitch.Data;
using HealthSwitch.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Endpoints;

public static class Dtos
{
    public static string TypeLabel(Organisation o) => o.Type switch
    {
        OrgType.Hmo => "HMO",
        OrgType.StateScheme => "State scheme",
        OrgType.Hospital => "Hospital",
        _ => o.Type.ToString(),
    };

    public static object Org(Organisation o) => new
    {
        code = o.Code, name = o.Name, shortName = o.ShortName, type = TypeLabel(o),
        mode = o.Mode == ConnectionMode.Api ? "api" : "portal", state = o.State, online = o.Online,
    };

    /// <summary>Loads claims with everything the portal shows, without loading attachment bytes.</summary>
    public static async Task<List<object>> Claims(AppDb db, IQueryable<Claim> query, CancellationToken ct)
    {
        var claims = await query.Include(c => c.Items).Include(c => c.Remarks).Include(c => c.Events)
            .AsSplitQuery().AsNoTracking().ToListAsync(ct);
        if (claims.Count == 0) return new List<object>();
        var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
        var tariffs = await db.Services.AsNoTracking().ToDictionaryAsync(s => s.Code, s => s.Tariff, ct);
        var ids = claims.Select(c => c.Id).ToList();
        var ledger = await db.Ledger.AsNoTracking().Where(l => ids.Contains(l.ClaimId)).ToDictionaryAsync(l => l.ClaimId, ct);
        var itemIds = claims.SelectMany(c => c.Items.Select(i => i.Id)).ToList();
        var files = (await db.Attachments.AsNoTracking().Where(a => itemIds.Contains(a.LineItemId))
                .Select(a => new { a.Id, a.LineItemId, a.FileName, a.ContentType, a.Size, a.UploadedAt }).ToListAsync(ct))
            .ToLookup(a => a.LineItemId);

        return claims.Select(c =>
        {
            ledger.TryGetValue(c.Id, out var l);
            var (label, kind) = ClaimRules.HospitalStatus(c, l);
            var h = orgs.GetValueOrDefault(c.HospitalCode);
            var p = orgs.GetValueOrDefault(c.PayerCode);
            return (object)new
            {
                id = c.Id, authNo = c.AuthNo,
                hospitalCode = c.HospitalCode, hospitalName = h?.Name, hospitalShort = h?.ShortName,
                payerCode = c.PayerCode, payerName = p?.Name, payerMode = p?.Mode == ConnectionMode.Api ? "api" : "portal",
                memberNo = c.MemberNo, nin = c.Nin, patientName = c.PatientName,
                diagnosis = new { code = c.DiagnosisCode, desc = c.DiagnosisText },
                consentRef = c.ConsentRef, stage = c.Stage, delivery = c.Delivery, awaitingPayer = c.AwaitingPayer,
                submittedAt = c.SubmittedAt, decidedAt = c.DecidedAt,
                rollup = ClaimRules.RollUp(c.Items), statusLabel = label, statusKind = kind,
                requestedTotal = c.Items.Sum(i => i.Requested), approvedTotal = ClaimRules.ApprovedTotal(c.Items),
                ledger = l is null ? null : new { amount = l.Amount, status = l.Status, batchNo = l.BatchNo },
                items = c.Items.OrderBy(i => i.Seq).Select(i => new
                {
                    id = i.Id, seq = i.Seq, code = i.Code, name = i.Name, tariff = tariffs.GetValueOrDefault(i.Code),
                    requested = i.Requested, approved = i.Approved, status = i.Status, reason = i.Reason,
                    docType = i.DocType, note = i.Note, response = i.Response,
                    attachments = files[i.Id].Select(a => new { id = a.Id, fileName = a.FileName, contentType = a.ContentType, size = a.Size }),
                }),
                remarks = c.Remarks.OrderBy(r => r.At).Select(r => new { by = r.By, text = r.Text, at = r.At }),
                events = c.Events.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Select(e => new { text = e.Text, at = e.At }),
            };
        }).ToList();
    }

    public static object Ledger(LedgerEntry l, IReadOnlyDictionary<string, Organisation> orgs) => new
    {
        id = l.Id, claimId = l.ClaimId, authNo = l.AuthNo, hospitalCode = l.HospitalCode,
        hospitalName = orgs.GetValueOrDefault(l.HospitalCode)?.ShortName, payerCode = l.PayerCode,
        payerName = orgs.GetValueOrDefault(l.PayerCode)?.Name, patientName = l.PatientName, amount = l.Amount,
        status = l.Status, batchNo = l.BatchNo, finalizedAt = l.FinalizedAt, paidAt = l.PaidAt,
    };
}
