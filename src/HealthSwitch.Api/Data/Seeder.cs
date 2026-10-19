using HealthSwitch.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HealthSwitch.Data;

/// <summary>Creates the schema and fills it with fictional sample data on first start.</summary>
public static class Seeder
{
    public const string Hospital = "FAC-LA-00231";
    public const string Riverside = "FAC-RI-00077";
    public const string Sapphire = "HMO-0XA";
    public const string Northstar = "HMO-0XB";
    public const string Coastal = "SSHIA-0XC";
    public const string SwitchCode = "SWITCH";
    public const string Processor = "PROCESSOR";
    /// <summary>The two hospitals that have their own demo portal.</summary>
    public static readonly string[] PortalHospitals = { Hospital, Riverside };
    /// <summary>Requests below this total are decided by Sapphire's own rules engine without a reviewer.</summary>
    public const long SapphireAutoLimit = 50_000;
    /// <summary>Bump when the schema or sample data changes: the next start rebuilds the demo database.</summary>
    public const string SeedVersion = "2";

    public static async Task InitAsync(IServiceProvider services, ILogger log)
    {
        // Free databases (Neon) suspend when idle, so the first connection can be slow: retry a few times.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var version = await CurrentVersionAsync(db);
                if (version != SeedVersion)
                {
                    log.LogWarning("Sample data version {Found} found, {Wanted} wanted: rebuilding the demo database", version ?? "none", SeedVersion);
                    await ResetAsync(db);
                }
                return;
            }
            catch (Exception ex) when (attempt < 6)
            {
                log.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
    }

    static async Task<string?> CurrentVersionAsync(AppDb db)
    {
        try
        {
            return await db.Database.SqlQueryRaw<string>("SELECT \"Value\" FROM \"AppInfo\" WHERE \"Key\" = 'seed_version'").FirstOrDefaultAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null; // first start, or a database from before versioning
        }
    }

    /// <summary>Drops the demo tables, recreates them and loads fresh sample data.</summary>
    public static async Task ResetAsync(AppDb db)
    {
        foreach (var table in db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>().Distinct())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"" + table + "\" CASCADE");
        foreach (var seq in db.Model.GetSequences())
            await db.Database.ExecuteSqlRawAsync("DROP SEQUENCE IF EXISTS \"" + seq.Name + "\"");
        db.ChangeTracker.Clear();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);
    }

    static Organisation Org(string code, string name, string shortName, OrgType type, ConnectionMode mode, string state = "") =>
        new() { Code = code, Name = name, ShortName = shortName, Type = type, Mode = mode, State = state };

    public static readonly (string Code, string Desc)[] Diagnoses =
    {
        ("K35.8", "Acute appendicitis"), ("O82", "Caesarean delivery"), ("B54", "Malaria, unspecified"),
        ("I10", "Essential hypertension"), ("E11.9", "Type 2 diabetes"), ("J18.9", "Pneumonia, unspecified"), ("R51", "Headache"),
        ("S62.1", "Fracture of wrist"),
    };

    static async Task SeedAsync(AppDb db)
    {
        db.Organisations.AddRange(
            Org(Hospital, "Palmgrove Hospital, Ikeja", "Palmgrove Hospital", OrgType.Hospital, ConnectionMode.Portal, "Lagos"),
            Org("FAC-LA-00418", "St. Brendan's Clinic, Yaba", "St. Brendan's Clinic", OrgType.Hospital, ConnectionMode.Portal, "Lagos"),
            Org("FAC-RI-00077", "Riverside Medical, Port Harcourt", "Riverside Medical", OrgType.Hospital, ConnectionMode.Portal, "Rivers"),
            Org("FAC-DE-00152", "Unity Specialist Hospital, Asaba", "Unity Specialist", OrgType.Hospital, ConnectionMode.Portal, "Delta"),
            Org("FAC-FC-00310", "Cedar Medical Centre, Abuja", "Cedar Medical Centre", OrgType.Hospital, ConnectionMode.Portal, "FCT"),
            Org(Sapphire, "Sapphire HMO", "Sapphire HMO", OrgType.Hmo, ConnectionMode.Api),
            Org(Northstar, "Northstar HMO", "Northstar HMO", OrgType.Hmo, ConnectionMode.Portal),
            Org(Coastal, "Coastal State Health Scheme", "Coastal State Scheme", OrgType.StateScheme, ConnectionMode.Portal),
            Org(SwitchCode, "Switch (member list)", "Switch", OrgType.Switch, ConnectionMode.Portal),
            Org(Processor, "Payment processor (sandbox)", "Processor", OrgType.Processor, ConnectionMode.Portal));

        db.Members.AddRange(
            new Member { Nin = "41820937561", MemberNo = "SAP-22041-01", Name = "Adaeze Okafor", PayerCode = Sapphire, Plan = "Gold Family", Active = true },
            new Member { Nin = "52937108264", MemberNo = "NST-55120-01", Name = "Tunde Bakare", PayerCode = Northstar, Plan = "Basic Plus", Active = true },
            new Member { Nin = "63048219375", MemberNo = "CSH-90311-02", Name = "Fatima Bello", PayerCode = Coastal, Plan = "State Formal Sector", Active = true },
            new Member { Nin = "74159320486", MemberNo = "NST-40877-01", Name = "Musa Ibrahim", PayerCode = Northstar, Plan = "Basic", Active = false, ExpiredOn = new DateOnly(2026, 9, 30) },
            new Member { Nin = "85260431597", MemberNo = "SAP-30117-01", Name = "Emeka Nwosu", PayerCode = Sapphire, Plan = "Silver", Active = true },
            new Member { Nin = "96371542608", MemberNo = "NST-61204-01", Name = "Halima Danjuma", PayerCode = Northstar, Plan = "Basic Plus", Active = true });

        var services = new[]
        {
            new ServiceItem { Code = "CONS-01", Name = "Specialist consultation", Tariff = 15000 },
            new ServiceItem { Code = "LAB-12", Name = "Full blood count", Tariff = 6500 },
            new ServiceItem { Code = "LAB-31", Name = "Lipid profile", Tariff = 18500 },
            new ServiceItem { Code = "IMG-04", Name = "Chest X-ray", Tariff = 25000 },
            new ServiceItem { Code = "IMG-21", Name = "Abdominal ultrasound", Tariff = 30000 },
            new ServiceItem { Code = "IMG-40", Name = "MRI, brain", Tariff = 180000 },
            new ServiceItem { Code = "SUR-12", Name = "Appendectomy", Tariff = 320000 },
            new ServiceItem { Code = "OBS-07", Name = "Caesarean section", Tariff = 450000 },
            new ServiceItem { Code = "WRD-01", Name = "Ward stay, per day", Tariff = 20000 },
            new ServiceItem { Code = "DRG-03", Name = "Antimalarial course (ACT)", Tariff = 4500 },
        };
        db.Services.AddRange(services);
        var svc = services.ToDictionary(s => s.Code);
        var now = DateTimeOffset.UtcNow;

        Claim Mk(string auth, string hosp, string payer, string patient, string member, string nin, string dx, double minutesAgo, params (string Code, long Amount)[] lines)
        {
            var d = Diagnoses.First(x => x.Code == dx);
            var c = new Claim
            {
                AuthNo = auth, HospitalCode = hosp, PayerCode = payer, PatientName = patient, MemberNo = member, Nin = nin,
                DiagnosisCode = d.Code, DiagnosisText = d.Desc, ConsentRef = "CONSENT-SAMPLE", SubmittedAt = now.AddMinutes(-minutesAgo), IsSample = true,
            };
            var seq = 1;
            foreach (var (code, amount) in lines)
                c.Items.Add(new LineItem { ClaimId = c.Id, Seq = seq++, Code = code, Name = svc[code].Name, Requested = amount });
            return c;
        }
        void Approve(Claim c, double hoursAfter)
        {
            foreach (var i in c.Items) { i.Status = ItemStatus.Approved; i.Approved = i.Requested; }
            c.DecidedAt = c.SubmittedAt.AddHours(hoursAfter);
        }
        LedgerEntry Ledger(Claim c, bool paid, double daysToPay = 10)
        {
            c.Stage = Stages.Final;
            var fin = (c.DecidedAt ?? c.SubmittedAt).AddHours(6);
            var l = new LedgerEntry
            {
                ClaimId = c.Id, AuthNo = c.AuthNo, HospitalCode = c.HospitalCode, PayerCode = c.PayerCode, PatientName = c.PatientName,
                Amount = ClaimRules.ApprovedTotal(c.Items), FinalizedAt = fin, IsSample = true,
            };
            if (paid && fin.AddDays(daysToPay) < now) { l.Status = "paid"; l.PaidAt = fin.AddDays(daysToPay); }
            return l;
        }

        // The named examples shown in the demo.
        var a = Mk("AUTH-2026-1038", Hospital, Northstar, "Grace Okonkwo", "NST-31877-01", "30418827651", "I10", 2900, ("CONS-01", 15000), ("LAB-31", 18500), ("IMG-04", 25000));
        a.Items[0].Status = a.Items[1].Status = ItemStatus.Approved; a.Items[0].Approved = 15000; a.Items[1].Approved = 18500;
        a.Items[2].Status = ItemStatus.Declined; a.Items[2].Approved = 0; a.Items[2].Reason = "Not medically indicated"; a.DecidedAt = a.SubmittedAt.AddHours(5);
        var b = Mk("AUTH-2026-1039", Hospital, Coastal, "Ibrahim Sule", "CSH-77120-01", "29117760432", "B54", 2400, ("CONS-01", 15000), ("DRG-03", 4500), ("WRD-01", 40000)); Approve(b, 9);
        var c1 = Mk("AUTH-2026-1040", Hospital, Northstar, "Blessing Eze", "NST-60231-02", "50821993470", "J18.9", 1500, ("CONS-01", 15000), ("IMG-04", 25000), ("WRD-01", 60000)); Approve(c1, 4);
        var d1 = Mk("AUTH-2026-1041", Hospital, Sapphire, "Chidinma Obi", "SAP-18802-01", "61120938845", "E11.9", 900, ("CONS-01", 15000), ("LAB-31", 18500)); Approve(d1, 0.05);
        var e1 = Mk("AUTH-2026-1042", Hospital, Northstar, "Samuel Okafor", "NST-48810-01", "47712093318", "S62.1", 140, ("CONS-01", 15000), ("IMG-04", 25000));
        e1.Items[0].Status = ItemStatus.Approved; e1.Items[0].Approved = 15000;
        e1.Items[1].Status = ItemStatus.Query; e1.Items[1].DocType = "Referral letter"; e1.Items[1].Note = "Please attach the GP referral letter. The X-ray request needs a clinical indication.";
        e1.Remarks.Add(new Remark { ClaimId = e1.Id, By = "Northstar HMO · claims reviewer", Text = "Consultation approved. X-ray held until we receive the referral letter.", At = now.AddMinutes(-120) });
        // Riverside Medical's named examples.
        var f1 = Mk("AUTH-2026-1031", Riverside, Northstar, "Folake Adisa", "NST-22918-01", "33310298874", "R51", 75, ("CONS-01", 15000), ("IMG-40", 210000)); f1.AwaitingPayer = true;
        var r2 = Mk("AUTH-2026-1032", Riverside, Sapphire, "Ebiere Tamuno", "SAP-41187-01", "40391827756", "O82", 55, ("OBS-07", 450000), ("WRD-01", 60000)); r2.AwaitingPayer = true;
        var r4 = Mk("AUTH-2026-1034", Riverside, Northstar, "Ibinabo George", "NST-70312-01", "52210938471", "B54", 3100, ("CONS-01", 15000), ("LAB-12", 6500), ("DRG-03", 4500)); Approve(r4, 6);
        var r5 = Mk("AUTH-2026-1035", Riverside, Sapphire, "Tamuno Briggs", "SAP-27719-01", "61820394475", "I10", 2600, ("CONS-01", 15000), ("LAB-31", 18500)); Approve(r5, 0.03);
        var r6 = Mk("AUTH-2026-1036", Riverside, Northstar, "Soye Jack", "NST-38821-02", "70931248856", "S62.1", 4300, ("CONS-01", 15000), ("IMG-04", 25000));
        r6.Items[0].Status = ItemStatus.Approved; r6.Items[0].Approved = 15000; r6.Items[1].Status = ItemStatus.Declined; r6.Items[1].Approved = 0; r6.Items[1].Reason = "Not covered by plan"; r6.DecidedAt = r6.SubmittedAt.AddHours(7);
        // Palmgrove request waiting for Sapphire's medical reviewer.
        var p7 = Mk("AUTH-2026-1037", Hospital, Sapphire, "Chukwuma Obi", "SAP-50926-01", "38821047763", "K35.8", 35, ("SUR-12", 320000), ("WRD-01", 60000)); p7.AwaitingPayer = true;
        var g1 = Mk("AUTH-2026-1033", "FAC-DE-00152", Coastal, "Ngozi Eze", "CSH-11873-01", "58823019944", "O82", 40, ("OBS-07", 450000), ("WRD-01", 60000)); g1.AwaitingPayer = true;
        foreach (var x in new[] { a, b, c1, d1, e1, r4, r5, r6 }) x.Log("Sample claim loaded");
        f1.Log("Placed in Northstar HMO review inbox"); g1.Log("Placed in Coastal State Health Scheme review inbox");
        foreach (var x in new[] { r2, p7 })
            x.Log($"Sapphire rules engine: ₦{x.Items.Sum(i => i.Requested):N0} is over the ₦{SapphireAutoLimit:N0} automatic limit; referred to a medical reviewer");
        r5.Remarks.Add(new Remark { ClaimId = r5.Id, By = "Sapphire HMO · automated rules", Text = "Decided automatically: each service approved up to the agreed tariff.", At = r5.DecidedAt!.Value });
        d1.Remarks.Add(new Remark { ClaimId = d1.Id, By = "Sapphire HMO · automated rules", Text = "Decided automatically: each service approved up to the agreed tariff.", At = d1.DecidedAt!.Value });
        db.Claims.AddRange(a, b, c1, d1, e1, f1, g1, r2, r4, r5, r6, p7);
        var la = Ledger(a, false); var lb = Ledger(b, true, 12); lb.Status = "paid"; lb.PaidAt ??= now.AddDays(-1);
        var l6 = Ledger(r6, true, 9); l6.Status = "paid"; l6.PaidAt ??= now.AddDays(-2);
        db.Ledger.AddRange(la, lb, Ledger(c1, false), Ledger(d1, false), Ledger(r4, false), Ledger(r5, false), l6);

        // Historical sample claims so the NHIA view has a realistic month of data.
        var rnd = new Random(42);
        var hospitals = new[] { (Hospital, 4), ("FAC-LA-00418", 3), ("FAC-RI-00077", 3), ("FAC-DE-00152", 4), ("FAC-FC-00310", 3) };
        var payers = new[] { (Sapphire, 3), (Northstar, 5), (Coastal, 4) };
        string Pick((string, int)[] w) { var t = w.Sum(x => x.Item2); var r = rnd.Next(t); foreach (var (k, n) in w) { if (r < n) return k; r -= n; } return w[0].Item1; }
        var first = new[] { "Amina", "Chinedu", "Ifeoma", "Kunle", "Halima", "Emeka", "Zainab", "Tobi", "Ngozi", "Bayo", "Aisha", "Uche", "Funmi", "Yusuf", "Kemi" };
        var last = new[] { "Adeyemi", "Okeke", "Bello", "Nwosu", "Lawal", "Eze", "Abubakar", "Ogunleye", "Okoro", "Danjuma", "Ade", "Uzor" };
        var dxCodes = Diagnoses.Select(x => x.Code).ToArray();
        for (var i = 0; i < 180; i++)
        {
            var hosp = Pick(hospitals); var payer = Pick(payers);
            var count = rnd.Next(1, 4);
            var lines = Enumerable.Range(0, count).Select(_ => services[rnd.Next(services.Length)]).DistinctBy(s => s.Code)
                .Select(s => (s.Code, rnd.NextDouble() < 0.2 ? (long)(s.Tariff * 1.15) : s.Tariff)).ToArray();
            var cl = Mk($"AUTH-2026-0{101 + i}", hosp, payer, $"{first[rnd.Next(first.Length)]} {last[rnd.Next(last.Length)]}",
                $"{payer[..3]}-{rnd.Next(10000, 99999)}-01", rnd.NextInt64(10000000000, 99999999999).ToString(), dxCodes[rnd.Next(dxCodes.Length)],
                rnd.Next(60 * 24, 60 * 24 * 30), lines);
            var hours = payer == Sapphire ? 0.02 + rnd.NextDouble() * 0.15 : payer == Northstar ? 1 + rnd.NextDouble() * 11 : 3 + rnd.NextDouble() * 15;
            var roll = rnd.NextDouble();
            if (roll < 0.06) { cl.Items[^1].Status = ItemStatus.Query; cl.Items[^1].DocType = "Lab result"; db.Claims.Add(cl); continue; }
            foreach (var it in cl.Items)
            {
                if (roll < 0.72) { it.Status = ItemStatus.Approved; it.Approved = Math.Min(it.Requested, svc[it.Code].Tariff); }
                else if (roll < 0.88) { var ok = it.Seq == 1; it.Status = ok ? ItemStatus.Approved : ItemStatus.Declined; it.Approved = ok ? it.Requested : 0; if (!ok) it.Reason = "Not covered by plan"; }
                else { it.Status = ItemStatus.Declined; it.Approved = 0; it.Reason = "Not medically indicated"; }
            }
            cl.DecidedAt = cl.SubmittedAt.AddHours(hours);
            db.Claims.Add(cl);
            if (roll < 0.88 && ClaimRules.ApprovedTotal(cl.Items) > 0)
            {
                var days = payer == Sapphire ? 5 + rnd.Next(8) : payer == Northstar ? 10 + rnd.Next(15) : 15 + rnd.Next(20);
                db.Ledger.Add(Ledger(cl, rnd.NextDouble() < 0.75, days));
            }
        }
        db.AppInfo.Add(new AppInfo { Key = "seed_version", Value = SeedVersion });
        await db.SaveChangesAsync();
    }
}
