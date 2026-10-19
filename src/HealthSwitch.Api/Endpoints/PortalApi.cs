using System.Text;
using HealthSwitch.Auth;
using HealthSwitch.Data;
using HealthSwitch.Domain;
using HealthSwitch.Gateway;
using HealthSwitch.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Endpoints;

/// <summary>
/// The API behind the web portal. Every action that crosses organisations goes through the
/// gateway and the queues, exactly as an integrated system would.
/// </summary>
public static class PortalApi
{
    public static readonly string[] DeclineReasons = { "Not covered by plan", "Above plan limit", "Not medically indicated", "Duplicate request" };
    public static readonly string[] DocTypes = { "Referral letter", "Lab result", "Imaging report", "Prior treatment proof" };
    const long MaxFileBytes = 5 * 1024 * 1024;

    static IResult Bad(string message, int status = 400) => Results.Json(new { error = new { message } }, statusCode: status);
    static IResult Forbidden() => Bad("Your organisation cannot see or change this.", 403);

    // Who may do what. Every /api route (apart from login) runs with a signed-in session.
    static Session Me(HttpContext ctx) => Tokens.Of(ctx) ?? throw new BadHttpRequestException("Sign in first.", 401);
    static bool IsHospital(HttpContext ctx, string code) => Me(ctx) is { Role: Role.Hospital } s && s.OrgCode == code;
    static bool IsPayer(HttpContext ctx, string code) => Me(ctx) is { Role: Role.Payer } s && s.OrgCode == code;
    static bool IsSwitch(HttpContext ctx) => Me(ctx).Role == Role.Switch;
    static bool CanSee(HttpContext ctx, Claim c) => Me(ctx) switch
    {
        { Role: Role.Hospital } s => c.HospitalCode == s.OrgCode,
        { Role: Role.Payer } s => c.PayerCode == s.OrgCode,
        { Role: Role.Switch } => true,
        _ => false,
    };
    /// <summary>Hides the 180 historical sample claims from portal lists; they only feed the regulator view.</summary>
    static IQueryable<Claim> Featured(IQueryable<Claim> q) => q.Where(c => !c.IsSample || c.AuthNo.StartsWith("AUTH-2026-10"));

    public record EligibilityRequest(string Lookup);
    public record LineRequest(string Code, long Amount);
    public record PreAuthRequest(string MemberNo, bool Consent, string DiagnosisCode, List<LineRequest> Lines);
    public record ItemDecision(Guid LineId, string Decision, string? Reason, string? DocType, string? Note);
    public record ReviewRequest(List<ItemDecision> Decisions, string? Remark, string? Reviewer);
    public record FinalDecisionRequest(bool Approve);
    public record BatchRequest(string HospitalCode, List<string> AuthNos);
    public record OnlineRequest(bool Online);
    public record LoginRequest(string Portal, string UserName, string Password);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------- sign-in ----------
        api.MapGet("/portals", () => Results.Ok(Portals.All.Select(p => new
        {
            slug = p.Slug, name = p.Name, kind = p.Kind, role = p.Role.ToString().ToLowerInvariant(),
            userName = p.UserName, password = p.Password, userDisplay = p.UserDisplay, userTitle = p.UserTitle, blurb = p.Blurb,
        })));

        api.MapPost("/login", (LoginRequest body, IConfiguration config) =>
        {
            var p = Portals.Find(body.Portal);
            if (p is null) return Bad("Unknown portal.", 404);
            var ok = string.Equals(body.UserName?.Trim(), p.UserName, StringComparison.OrdinalIgnoreCase)
                     && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(body.Password ?? ""), Encoding.UTF8.GetBytes(p.Password));
            if (!ok) return Bad("That email and password do not match.", 401);
            return Results.Ok(new { token = Tokens.Issue(config, p), userDisplay = p.UserDisplay, userTitle = p.UserTitle });
        });

        api.MapGet("/reference", async (HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            var me = Me(ctx);
            var portal = Portals.Find(me.Slug)!;
            var orgs = await db.Organisations.AsNoTracking().ToListAsync(ct);
            var demoPayers = new[] { Seeder.Northstar, Seeder.Sapphire };
            var mine = orgs.FirstOrDefault(x => x.Code == me.OrgCode);
            object orgDto = mine is null ? new { code = me.OrgCode, name = portal.Name } : Dtos.Org(mine);
            return Results.Ok(new
            {
                me = new { slug = me.Slug, role = me.Role.ToString().ToLowerInvariant(), user = me.User, userDisplay = portal.UserDisplay, userTitle = portal.UserTitle },
                org = orgDto,
                sapphireAutoLimit = Seeder.SapphireAutoLimit,
                hospitals = orgs.Where(o => o.Type == OrgType.Hospital).Select(Dtos.Org),
                payers = orgs.Where(o => o.Type is OrgType.Hmo or OrgType.StateScheme).Select(Dtos.Org),
                members = await db.Members.AsNoTracking()
                    .Where(m => me.Role == Role.Payer ? m.PayerCode == me.OrgCode : demoPayers.Contains(m.PayerCode))
                    .OrderBy(m => m.Name).ToListAsync(ct),
                services = await db.Services.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct),
                diagnoses = Seeder.Diagnoses.Select(d => new { code = d.Code, desc = d.Desc }),
                declineReasons = DeclineReasons,
                docTypes = DocTypes,
            });
        });

        // ---------- hospital ----------
        api.MapPost("/hospitals/{h}/eligibility", async (string h, EligibilityRequest body, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            if (!IsHospital(ctx, h)) return Forbidden();
            var q = (body.Lookup ?? "").Replace(" ", "").ToUpperInvariant();
            var m = await db.Members.FirstOrDefaultAsync(x => x.Nin == q || x.MemberNo == q, ct);
            if (m is null) return Bad("No member found with that NIN or member number.", 404);
            var corr = Guid.NewGuid();
            db.EligibilityChecks.Add(new EligibilityCheck { CorrelationId = corr, HospitalCode = h, MemberNo = m.MemberNo });
            await db.SaveChangesAsync(ct);
            await gw.AcceptAsync(new AcceptRequest("coverageeligibility.check", h, m.PayerCode, Fhir.Json(new EligibilityQuery(m.MemberNo, m.Nin)), CorrelationId: corr), ct);
            return Results.Ok(new { correlationId = corr, member = m });
        });

        api.MapGet("/eligibility/{corr:guid}", async (Guid corr, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            var c = await db.EligibilityChecks.AsNoTracking().FirstOrDefaultAsync(x => x.CorrelationId == corr, ct);
            if (c is not null && !IsHospital(ctx, c.HospitalCode)) return Forbidden();
            return c is null ? Results.NotFound() : Results.Ok(new { status = c.Status, via = c.Via });
        });

        api.MapPost("/hospitals/{h}/preauth", async (string h, PreAuthRequest body, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            if (!IsHospital(ctx, h)) return Forbidden();
            var m = await db.Members.FindAsync(new object[] { body.MemberNo }, ct);
            if (m is null) return Bad("Unknown member.");
            if (!m.Active) return Bad("This member's cover is not active.");
            if (!body.Consent) return Bad("Record the patient's consent before sending.");
            var lines = (body.Lines ?? new()).Where(l => l.Amount > 0).ToList();
            if (lines.Count == 0) return Bad("Add at least one service.");
            var services = await db.Services.ToDictionaryAsync(s => s.Code, ct);
            if (lines.Any(l => !services.ContainsKey(l.Code))) return Bad("Unknown service code.");
            var dx = Seeder.Diagnoses.FirstOrDefault(d => d.Code == body.DiagnosisCode);
            if (dx.Code is null) return Bad("Unknown diagnosis code.");
            var hosp = await db.Organisations.FindAsync(new object[] { h }, ct);
            var payer = await db.Organisations.FindAsync(new object[] { m.PayerCode }, ct);
            if (hosp is null || payer is null) return Bad("Unknown organisation.");

            var n = await db.NextAsync("auth_seq", ct);
            var claim = new Claim
            {
                AuthNo = $"AUTH-{DateTime.UtcNow.Year}-{n}", HospitalCode = h, PayerCode = m.PayerCode, MemberNo = m.MemberNo, Nin = m.Nin,
                PatientName = m.Name, DiagnosisCode = dx.Code, DiagnosisText = dx.Desc, Delivery = "sending",
                ConsentRef = "CONSENT-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8],
            };
            var seq = 1;
            foreach (var l in lines)
                claim.Items.Add(new LineItem { ClaimId = claim.Id, Seq = seq++, Code = l.Code, Name = services[l.Code].Name, Requested = l.Amount });
            claim.Log($"Pre-authorisation sent with patient consent {claim.ConsentRef}");
            db.Claims.Add(claim);
            await db.SaveChangesAsync(ct);
            await gw.AcceptAsync(new AcceptRequest("preauth.submit", h, m.PayerCode, Fhir.ClaimBundle(claim, hosp.Name, payer.Name, payer.Code, "preauthorization"),
                CorrelationId: claim.CorrelationId, WorkflowId: claim.WorkflowId, ClaimId: claim.Id, Track: true), ct);
            return Results.Ok(new { id = claim.Id, authNo = claim.AuthNo });
        });

        // Lists are always limited to the signed-in organisation's own claims.
        api.MapGet("/claims", async (bool? awaiting, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            var me = Me(ctx);
            var q = Featured(db.Claims.AsQueryable());
            if (me.Role == Role.Hospital) q = q.Where(c => c.HospitalCode == me.OrgCode);
            else if (me.Role == Role.Payer) q = q.Where(c => c.PayerCode == me.OrgCode);
            else if (me.Role != Role.Switch) return Forbidden();
            if (awaiting == true) q = q.Where(c => c.AwaitingPayer);
            q = awaiting == true ? q.OrderBy(c => c.SubmittedAt) : q.OrderByDescending(c => c.SubmittedAt);
            return Results.Ok(await Dtos.Claims(db, q.Take(50), ct));
        });

        api.MapGet("/claims/{id:guid}", async (Guid id, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            var claim = await db.Claims.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (claim is null) return Results.NotFound();
            if (!CanSee(ctx, claim)) return Forbidden();
            var list = await Dtos.Claims(db, db.Claims.Where(c => c.Id == id), ct);
            return Results.Ok(list[0]);
        });

        api.MapPost("/claims/{id:guid}/final", async (Guid id, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            var claim = await db.Claims.Include(c => c.Items).Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (claim is null) return Results.NotFound();
            if (!IsHospital(ctx, claim.HospitalCode)) return Forbidden();
            var roll = ClaimRules.RollUp(claim.Items);
            if (claim.Stage != Stages.PreAuth || roll is not ("approved" or "partial")) return Bad("Only an approved pre-authorisation can become a final claim.");
            var hosp = await db.Organisations.FindAsync(new object[] { claim.HospitalCode }, ct);
            var payer = await db.Organisations.FindAsync(new object[] { claim.PayerCode }, ct);
            claim.Stage = Stages.FinalClaim; claim.Delivery = "sending";
            claim.Log($"Final claim sent: ₦{ClaimRules.ApprovedTotal(claim.Items):N0} of approved services");
            await db.SaveChangesAsync(ct);
            await gw.AcceptAsync(new AcceptRequest("claim.submit", claim.HospitalCode, claim.PayerCode,
                Fhir.ClaimBundle(claim, hosp!.Name, payer!.Name, payer.Code, "claim"), WorkflowId: claim.WorkflowId, ClaimId: claim.Id, Track: true), ct);
            return Results.Ok();
        });

        // The hospital answers a query: files per line item (form field "item-{lineId}") plus an optional note.
        api.MapPost("/claims/{id:guid}/documents", async (Guid id, HttpRequest req, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            if (!req.HasFormContentType) return Bad("Send the documents as a multipart form.");
            var form = await req.ReadFormAsync(ct);
            var claim = await db.Claims.Include(c => c.Items).Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (claim is null) return Results.NotFound();
            if (!IsHospital(ctx, claim.HospitalCode)) return Forbidden();
            var queried = claim.Items.Where(i => i.Status == ItemStatus.Query).ToList();
            if (queried.Count == 0) return Bad("This claim has no open query.");
            var note = form["note"].ToString().Trim();
            var saved = new List<Attachment>();
            foreach (var item in queried)
            {
                var files = form.Files.GetFiles("item-" + item.Id);
                if (files.Count == 0) return Bad($"Attach at least one file for {item.Name}.");
                foreach (var f in files)
                {
                    if (f.Length > MaxFileBytes) return Bad($"{f.FileName} is larger than 5 MB.");
                    using var ms = new MemoryStream();
                    await f.CopyToAsync(ms, ct);
                    saved.Add(new Attachment { LineItemId = item.Id, FileName = Path.GetFileName(f.FileName), ContentType = string.IsNullOrEmpty(f.ContentType) ? "application/octet-stream" : f.ContentType, Size = f.Length, Data = ms.ToArray() });
                }
            }
            db.Attachments.AddRange(saved);
            foreach (var item in queried)
            {
                item.Status = ItemStatus.Pending; item.Response = note; item.Reason = "Documents received: " + item.DocType;
            }
            claim.Delivery = "sending";
            claim.Log("Documents sent: " + string.Join(", ", saved.Select(s => s.FileName)) + (note.Length > 0 ? " · Note: " + note : ""));
            await db.SaveChangesAsync(ct);
            await gw.AcceptAsync(new AcceptRequest("communication.on_request", claim.HospitalCode, claim.PayerCode, Fhir.Communication(claim, saved, note),
                CorrelationId: claim.CorrelationId, WorkflowId: claim.WorkflowId, ClaimId: claim.Id, Track: true), ct);
            return Results.Ok(new { files = saved.Count });
        }).DisableAntiforgery();

        api.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            var owner = await db.LineItems.AsNoTracking().Where(i => i.Id == a.LineItemId)
                .Join(db.Claims.AsNoTracking(), i => i.ClaimId, c => c.Id, (i, c) => c).FirstOrDefaultAsync(ct);
            if (owner is null || !CanSee(ctx, owner)) return Forbidden();
            var safeType = a.ContentType.StartsWith("image/") || a.ContentType == "application/pdf" ? a.ContentType : "application/octet-stream";
            return Results.File(a.Data, safeType, a.FileName, enableRangeProcessing: false);
        });

        api.MapGet("/hospitals/{h}/ledger", async (string h, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsHospital(ctx, h)) return Forbidden();
            var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
            var rows = await db.Ledger.AsNoTracking().Where(l => l.HospitalCode == h && (!l.IsSample || l.AuthNo.StartsWith("AUTH-2026-10")))
                .OrderByDescending(l => l.FinalizedAt).Take(100).ToListAsync(ct);
            var open = await db.Ledger.AsNoTracking().Where(l => l.HospitalCode == h && l.Status != "paid")
                .GroupBy(l => l.PayerCode).Select(g => new { payerCode = g.Key, amount = g.Sum(x => x.Amount), count = g.Count() }).ToListAsync(ct);
            return Results.Ok(new { entries = rows.Select(l => Dtos.Ledger(l, orgs)), outstanding = open });
        });

        // ---------- payer ----------
        api.MapPost("/claims/{id:guid}/review", async (Guid id, ReviewRequest body, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            var claim = await db.Claims.Include(c => c.Items).Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (claim is null) return Results.NotFound();
            if (!IsPayer(ctx, claim.PayerCode)) return Forbidden();
            if (!claim.AwaitingPayer || claim.Stage != Stages.PreAuth) return Bad("This request is not waiting for a review.");
            var payer = await db.Organisations.FindAsync(new object[] { claim.PayerCode }, ct);
            var byId = (body.Decisions ?? new()).ToDictionary(d => d.LineId);
            var decisions = new List<DecisionDto>();
            foreach (var i in claim.Items.OrderBy(x => x.Seq))
            {
                if (i.Status is ItemStatus.Approved or ItemStatus.Declined) { decisions.Add(new DecisionDto(i.Id, i.Status, i.Approved, i.Reason, i.DocType, i.Note)); continue; }
                if (!byId.TryGetValue(i.Id, out var d)) return Bad($"Decide {i.Name} before submitting.");
                decisions.Add(d.Decision switch
                {
                    "approve" => new DecisionDto(i.Id, ItemStatus.Approved, i.Requested, "", "", ""),
                    "decline" => new DecisionDto(i.Id, ItemStatus.Declined, 0, string.IsNullOrWhiteSpace(d.Reason) ? DeclineReasons[0] : d.Reason, "", ""),
                    "query" => new DecisionDto(i.Id, ItemStatus.Query, null, "", string.IsNullOrWhiteSpace(d.DocType) ? DocTypes[0] : d.DocType, (d.Note ?? "").Trim()),
                    _ => throw new BadHttpRequestException("Unknown decision " + d.Decision),
                });
            }
            var who = Portals.Find(Me(ctx).Slug);
            var reviewer = who is null ? "claims reviewer" : $"{who.UserDisplay}, {who.UserTitle.ToLowerInvariant()}";
            var remark = string.IsNullOrWhiteSpace(body.Remark) ? null : new RemarkDto($"{payer?.Name} · {reviewer}", body.Remark.Trim());
            claim.AwaitingPayer = false;
            claim.Log($"{payer?.Name}: decisions submitted by {reviewer}");
            await db.SaveChangesAsync(ct);
            var apiName = decisions.Any(d => d.Status == ItemStatus.Query) ? "communication.request" : "preauth.on_submit";
            await gw.AcceptAsync(new AcceptRequest(apiName, claim.PayerCode, claim.HospitalCode, Fhir.Json(new DecisionPayload(decisions, remark)),
                CorrelationId: claim.CorrelationId, WorkflowId: claim.WorkflowId, ClaimId: claim.Id), ct);
            return Results.Ok();
        });

        api.MapPost("/claims/{id:guid}/final-decision", async (Guid id, FinalDecisionRequest body, HttpContext ctx, AppDb db, GatewayService gw, CancellationToken ct) =>
        {
            var claim = await db.Claims.Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (claim is null) return Results.NotFound();
            if (!IsPayer(ctx, claim.PayerCode)) return Forbidden();
            if (!claim.AwaitingPayer || claim.Stage != Stages.FinalClaim) return Bad("This claim is not waiting for a final decision.");
            claim.AwaitingPayer = false;
            claim.Log(body.Approve ? "Payer approved the final claim" : "Payer declined the final claim");
            await db.SaveChangesAsync(ct);
            await gw.AcceptAsync(new AcceptRequest("claim.on_submit", claim.PayerCode, claim.HospitalCode, Fhir.Json(new OutcomePayload(body.Approve ? "approved" : "declined")),
                WorkflowId: claim.WorkflowId, ClaimId: claim.Id), ct);
            return Results.Ok();
        });

        api.MapGet("/payers/{p}/ledger", async (string p, string? hospital, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsPayer(ctx, p)) return Forbidden();
            var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
            var hospitals = await db.Ledger.AsNoTracking().Where(l => l.PayerCode == p && l.Status == "unpaid").Select(l => l.HospitalCode).Distinct().ToListAsync(ct);
            var q = db.Ledger.AsNoTracking().Where(l => l.PayerCode == p && l.Status == "unpaid");
            if (!string.IsNullOrEmpty(hospital)) q = q.Where(l => l.HospitalCode == hospital);
            var rows = await q.OrderBy(l => l.FinalizedAt).Take(60).ToListAsync(ct);
            return Results.Ok(new { hospitals = hospitals.Select(h => orgs.TryGetValue(h, out var o) ? Dtos.Org(o) : null).Where(x => x is not null), entries = rows.Select(l => Dtos.Ledger(l, orgs)) });
        });

        api.MapGet("/payers/{p}/batches", async (string p, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsPayer(ctx, p)) return Forbidden();
            var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
            var rows = await db.Batches.AsNoTracking().Where(b => b.PayerCode == p).OrderByDescending(b => b.CreatedAt).Take(20).ToListAsync(ct);
            return Results.Ok(rows.Select(b => new
            {
                b.BatchNo, b.Total, b.VirtualAccount, b.Status, b.CreatedAt, b.ExpiresAt, b.PaidAt,
                hospitalName = orgs.GetValueOrDefault(b.HospitalCode)?.Name, authNos = b.AuthNosCsv.Split(',', StringSplitOptions.RemoveEmptyEntries),
                bank = "Sandbox processor bank",
            }));
        });

        api.MapPost("/payers/{p}/batches", async (string p, BatchRequest body, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsPayer(ctx, p)) return Forbidden();
            var auths = (body.AuthNos ?? new()).Distinct().ToList();
            var entries = await db.Ledger.Where(l => l.PayerCode == p && l.HospitalCode == body.HospitalCode && l.Status == "unpaid" && auths.Contains(l.AuthNo)).ToListAsync(ct);
            if (entries.Count == 0 || entries.Count != auths.Count) return Bad("Select approved, unpaid claims for one hospital only.");
            var n = await db.NextAsync("batch_seq", ct);
            var batch = new PaymentBatch
            {
                BatchNo = $"BATCH-{DateTime.UtcNow.Year}-{n:D4}", PayerCode = p, HospitalCode = body.HospitalCode, Total = entries.Sum(e => e.Amount),
                VirtualAccount = "99" + Random.Shared.Next(10000000, 99999999), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                AuthNosCsv = string.Join(',', entries.Select(e => e.AuthNo)),
            };
            foreach (var e in entries) { e.Status = "invoiced"; e.BatchNo = batch.BatchNo; }
            db.Batches.Add(batch);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { batch.BatchNo, batch.Total, batch.VirtualAccount });
        });

        // Demo control: plays the payment processor and calls our signed webhook, as a real processor would.
        api.MapPost("/batches/{batchNo}/simulate-payment", async (string batchNo, HttpContext ctx, AppDb db, IHttpClientFactory http, IConfiguration config, CancellationToken ct) =>
        {
            var batch = await db.Batches.FirstOrDefaultAsync(b => b.BatchNo == batchNo, ct);
            if (batch is null) return Results.NotFound();
            if (!IsPayer(ctx, batch.PayerCode) && !IsSwitch(ctx)) return Forbidden();
            if (batch.Status != "pending") return Bad("This batch is not awaiting payment.");
            batch.Status = "confirming";
            await db.SaveChangesAsync(ct);
            var body = Fhir.Json(new { account = batch.VirtualAccount, amount = batch.Total, reference = "NIP-" + Random.Shared.NextInt64(100000000000, 999999999999) });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{config["SelfBaseUrl"]}/v1/webhooks/processor") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Signature", Signature.Sign(Signature.Secret(config), body));
            using var res = await http.CreateClient("payer").SendAsync(req, ct);
            return res.IsSuccessStatusCode ? Results.Ok() : Bad("The processor webhook was refused.", 502);
        });

        // ---------- switch monitor ----------
        api.MapGet("/switch/overview", async (HttpContext ctx, AppDb db, IConfiguration config, CancellationToken ct) =>
        {
            if (!IsSwitch(ctx)) return Forbidden();
            var since = DateTimeOffset.UtcNow.AddHours(-12);
            var active = await db.Envelopes.AsNoTracking().Where(e => e.Status != "delivered" && e.Status != "dead")
                .GroupBy(e => e.Stage).Select(g => new { stage = g.Key, count = g.Count() }).ToListAsync(ct);
            var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
            var recent = await db.Envelopes.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(30).ToListAsync(ct);
            var dead = await db.Envelopes.AsNoTracking().Where(e => e.Status == "dead").OrderByDescending(e => e.UpdatedAt).Take(20).ToListAsync(ct);
            object Msg(MessageEnvelope e) => new
            {
                id = e.Id, messageNo = e.MessageNo, api = e.Api, status = e.Status, stage = e.Stage, attempts = e.Attempts, error = e.Error,
                fromName = orgs.GetValueOrDefault(e.SenderCode)?.ShortName ?? e.SenderCode, toName = orgs.GetValueOrDefault(e.RecipientCode)?.ShortName ?? e.RecipientCode,
                createdAt = e.CreatedAt,
                envelope = new Dictionary<string, object?>
                {
                    ["x-hcx-sender_code"] = e.SenderCode, ["x-hcx-recipient_code"] = e.RecipientCode, ["x-hcx-api_call_id"] = e.ApiCallId,
                    ["x-hcx-correlation_id"] = e.CorrelationId, ["x-hcx-workflow_id"] = e.WorkflowId, ["x-hcx-status"] = e.Status,
                    ["payload"] = System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.Clone(),
                },
            };
            return Results.Ok(new
            {
                transport = string.IsNullOrWhiteSpace(config["RABBITMQ_URL"]) ? "in-memory" : "RabbitMQ",
                stages = active,
                deliveredLast12h = await db.Envelopes.CountAsync(e => e.Status == "delivered" && e.UpdatedAt > since, ct),
                recent = recent.Select(Msg), dead = dead.Select(Msg),
                participants = orgs.Values.Where(o => o.Type is OrgType.Hmo or OrgType.StateScheme || Seeder.PortalHospitals.Contains(o.Code))
                    .OrderBy(o => o.Type).ThenBy(o => o.Name).Select(Dtos.Org),
            });
        });

        api.MapPost("/switch/participants/{code}/online", async (string code, OnlineRequest body, HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsSwitch(ctx)) return Forbidden();
            var org = await db.Organisations.FindAsync(new object[] { code }, ct);
            if (org is null) return Results.NotFound();
            if (org.Mode != ConnectionMode.Api) return Bad("Only API-connected participants can go offline.");
            org.Online = body.Online;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        api.MapPost("/switch/messages/{id:guid}/replay", async (Guid id, HttpContext ctx, AppDb db, ISendEndpointProvider send, CancellationToken ct) =>
        {
            if (!IsSwitch(ctx)) return Forbidden();
            var env = await db.Envelopes.FindAsync(new object[] { id }, ct);
            if (env is null) return Results.NotFound();
            if (env.Status != "dead") return Bad("Only dead-lettered messages can be replayed.");
            var recipient = await db.Organisations.FindAsync(new object[] { env.RecipientCode }, ct);
            var queue = recipient?.Mode == ConnectionMode.Api ? Queues.DeliverApi : Queues.DeliverPortal;
            env.Status = "dispatched"; env.Stage = queue; env.Attempts = 0; env.Error = null; env.UpdatedAt = DateTimeOffset.UtcNow;
            if (env.Track && env.ClaimId is { } cid) { var c = await db.Claims.FindAsync(new object[] { cid }, ct); if (c is not null) c.Delivery = "queued"; }
            await db.SaveChangesAsync(ct);
            var ep = await send.GetSendEndpoint(new Uri("queue:" + queue));
            await ep.Send(new DeliverMessage(env.Id), ct);
            return Results.Ok();
        });

        // Demo control: throws away everything done in the demo and reloads the sample data.
        api.MapPost("/switch/reset", async (HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (!IsSwitch(ctx)) return Forbidden();
            await Seeder.ResetAsync(db);
            return Results.Ok();
        });

        // ---------- NHIA (aggregated, no patient-level data) ----------
        api.MapGet("/nhia/summary", async (HttpContext ctx, AppDb db, CancellationToken ct) =>
        {
            if (Me(ctx).Role is not (Role.Regulator or Role.Switch)) return Forbidden();
            var orgs = await db.Organisations.AsNoTracking().ToDictionaryAsync(o => o.Code, ct);
            var since = DateTimeOffset.UtcNow.AddDays(-31);
            var claims = await db.Claims.AsNoTracking().Include(c => c.Items).Where(c => c.SubmittedAt > since).ToListAsync(ct);
            var ledger = await db.Ledger.AsNoTracking().Where(l => l.FinalizedAt > since.AddDays(-30)).ToListAsync(ct);
            string PayerType(string code) => orgs.TryGetValue(code, out var o)
                ? o.Type == OrgType.StateScheme ? "State scheme" : o.Mode == ConnectionMode.Api ? "HMO, connected by API" : "HMO, portal" : "Other";
            static double Median(List<double> v) { if (v.Count == 0) return 0; v.Sort(); return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }

            var byState = claims.GroupBy(c => orgs.GetValueOrDefault(c.HospitalCode)?.State ?? "Unknown")
                .Select(g => new { state = g.Key, count = g.Count(), live = g.Count(c => !c.IsSample) }).OrderByDescending(x => x.count);
            var decided = claims.Select(c => ClaimRules.RollUp(c.Items)).Where(r => r != "pending").ToList();
            var decisions = new[] { "approved", "partial", "declined", "query" }.Select(k => new { kind = k, count = decided.Count(r => r == k) });
            var hours = claims.Where(c => c.DecidedAt != null).Select(c => (c.DecidedAt!.Value - c.SubmittedAt).TotalHours).ToList();
            var now = DateTimeOffset.UtcNow;
            var open = ledger.Where(l => l.Status != "paid").Select(l => (now - l.FinalizedAt).TotalDays).ToList();
            var ageing = new[]
            {
                new { bucket = "0–15 days", count = open.Count(d => d < 15) },
                new { bucket = "15–30 days", count = open.Count(d => d >= 15 && d < 30) },
                new { bucket = "Over 30 days", count = open.Count(d => d >= 30) },
            };
            var types = claims.GroupBy(c => PayerType(c.PayerCode)).Select(g => new
            {
                type = g.Key, claims = g.Count(),
                medianHours = Median(g.Where(c => c.DecidedAt != null).Select(c => (c.DecidedAt!.Value - c.SubmittedAt).TotalHours).ToList()),
                avgDaysToPay = ledger.Where(l => l.PaidAt != null && PayerType(l.PayerCode) == g.Key).Select(l => (l.PaidAt!.Value - l.FinalizedAt).TotalDays).DefaultIfEmpty(0).Average(),
            }).OrderBy(x => x.type);
            return Results.Ok(new
            {
                totalClaims = claims.Count, liveClaims = claims.Count(c => !c.IsSample),
                approvalRate = decided.Count == 0 ? 0 : Math.Round(100.0 * decided.Count(r => r is "approved" or "partial") / decided.Count),
                decidedCount = decided.Count, medianHours = Median(hours), unpaidOver30 = open.Count(d => d >= 30),
                byState, decisions, ageing, types,
            });
        });
    }
}
