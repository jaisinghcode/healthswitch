using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HealthSwitch.Data;
using HealthSwitch.Endpoints;
using Microsoft.AspNetCore.WebUtilities;

namespace HealthSwitch.Auth;

public enum Role { Hospital, Payer, Switch, Regulator }

/// <summary>One demo website: which organisation it belongs to and its demo login.</summary>
public record Portal(string Slug, string OrgCode, Role Role, string Name, string Kind, string UserName, string Password, string UserDisplay, string UserTitle, string Blurb);

public static class Portals
{
    public static readonly Portal[] All =
    {
        new("palmgrove", Seeder.Hospital, Role.Hospital, "Palmgrove Hospital", "Hospital · Ikeja, Lagos",
            "billing@palmgrove.ng", "palmgrove2026", "Amaka Eze", "Billing officer",
            "Checks cover, sends pre-authorisations and claims to every HMO from one screen, answers queries and tracks payments."),
        new("riverside", Seeder.Riverside, Role.Hospital, "Riverside Medical", "Hospital · Port Harcourt, Rivers",
            "billing@riversidemedical.ng", "riverside2026", "Ibiere Briggs", "Billing officer",
            "A second hospital with its own login. It only ever sees its own patients and claims."),
        new("northstar", Seeder.Northstar, Role.Payer, "Northstar HMO", "HMO · reviews in the HealthSwitch portal",
            "claims@northstarhmo.ng", "northstar2026", "Dr. Kemi Adeyemi", "Claims reviewer",
            "Has no claims system of its own. Every request lands in this portal and a reviewer decides it."),
        new("sapphire", Seeder.Sapphire, Role.Payer, "Sapphire HMO", "HMO · own system, connected by API",
            "claims@sapphirehmo.ng", "sapphire2026", "Dr. Yusuf Danjuma", "Medical reviewer",
            "Its own system is connected to the switch by API. Requests under ₦50,000 are decided automatically; larger ones go to a medical reviewer."),
        new("switch", Seeder.SwitchCode, Role.Switch, "HealthSwitch Operations", "Switch operator console",
            "ops@healthswitch.ng", "switch2026", "Tolu Adebayo", "Operations duty officer",
            "Live message flow, queues and retries, participant status, dead-letter replay and demo reset."),
        new("nhia", "REGULATOR", Role.Regulator, "Regulator view", "Read-only oversight dashboard",
            "analyst@regulator.demo", "regulator2026", "Oversight analyst", "Read-only",
            "Totals, approval rates, turnaround and payment ageing. No patient names or diagnoses."),
    };

    public static Portal? Find(string? slug) => All.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Who is signed in, carried in a signed bearer token.</summary>
public record Session(string Slug, string OrgCode, Role Role, string User, long Exp);

public static class Tokens
{
    public const string ItemKey = "hs-session";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static byte[] Key(IConfiguration c) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("session|" + (c["SESSION_SECRET"] is { Length: > 0 } s ? s : Signature.Secret(c))));

    public static string Issue(IConfiguration c, Portal p)
    {
        var body = Base64UrlTextEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(
            new Session(p.Slug, p.OrgCode, p.Role, p.UserName, DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds()), Json));
        return body + "." + Base64UrlTextEncoder.Encode(HMACSHA256.HashData(Key(c), Encoding.UTF8.GetBytes(body)));
    }

    public static Session? Read(IConfiguration c, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var expected = HMACSHA256.HashData(Key(c), Encoding.UTF8.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Base64UrlTextEncoder.Decode(parts[1]))) return null;
            var s = JsonSerializer.Deserialize<Session>(Base64UrlTextEncoder.Decode(parts[0]), Json);
            return s is null || s.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? null : s;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    public static Session? Of(HttpContext ctx) => ctx.Items.TryGetValue(ItemKey, out var s) ? s as Session : null;

    /// <summary>Paths under /api that work without signing in.</summary>
    public static bool IsPublic(PathString p) =>
        p.StartsWithSegments("/api/meta") || p.StartsWithSegments("/api/login") || p.StartsWithSegments("/api/portals");

    public static string? FromRequest(HttpRequest req)
    {
        var h = req.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h[7..].Trim();
        var q = req.Query["token"].ToString();
        return string.IsNullOrEmpty(q) ? null : q;
    }
}
