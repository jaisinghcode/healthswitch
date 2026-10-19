using HealthSwitch.Auth;
using HealthSwitch.Data;
using Microsoft.Extensions.Configuration;
using HealthSwitch.Domain;
using HealthSwitch.Endpoints;
using Xunit;

namespace HealthSwitch.Tests;

public class RulesTests
{
    static LineItem Item(string status, long requested = 1000, long? approved = null) =>
        new() { Status = status, Requested = requested, Approved = approved ?? (status == ItemStatus.Approved ? requested : status == ItemStatus.Declined ? 0 : null) };

    [Fact]
    public void Any_query_means_query_raised() =>
        Assert.Equal("query", ClaimRules.RollUp(new[] { Item(ItemStatus.Approved), Item(ItemStatus.Query) }));

    [Fact]
    public void Pending_items_keep_the_claim_pending() =>
        Assert.Equal("pending", ClaimRules.RollUp(new[] { Item(ItemStatus.Approved), Item(ItemStatus.Pending) }));

    [Fact]
    public void All_approved_in_full_is_approved() =>
        Assert.Equal("approved", ClaimRules.RollUp(new[] { Item(ItemStatus.Approved), Item(ItemStatus.Approved) }));

    [Fact]
    public void Mixed_decisions_are_partial() =>
        Assert.Equal("partial", ClaimRules.RollUp(new[] { Item(ItemStatus.Approved), Item(ItemStatus.Declined) }));

    [Fact]
    public void Approved_below_requested_is_partial() =>
        Assert.Equal("partial", ClaimRules.RollUp(new[] { Item(ItemStatus.Approved, 1000, 800) }));

    [Fact]
    public void All_declined_is_declined() =>
        Assert.Equal("declined", ClaimRules.RollUp(new[] { Item(ItemStatus.Declined), Item(ItemStatus.Declined) }));

    [Fact]
    public void Worked_example_from_scope_section_9_4()
    {
        var items = new[] { Item(ItemStatus.Approved, 5000), Item(ItemStatus.Approved, 18500), Item(ItemStatus.Declined, 25000) };
        Assert.Equal(23500, ClaimRules.ApprovedTotal(items));
        Assert.Equal("partial", ClaimRules.RollUp(items));
    }

    [Fact]
    public void Tariff_rule_caps_at_tariff()
    {
        Assert.Equal(15000, ClaimRules.TariffRule(18000, 15000).Approved);
        Assert.Equal(12000, ClaimRules.TariffRule(12000, 15000).Approved);
    }

    [Fact]
    public void Neon_style_url_becomes_npgsql_string()
    {
        var cs = ConnectionStrings.FromUrl("postgresql://alex:p%40ss@ep-cool-123.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require");
        Assert.Contains("Host=ep-cool-123.eu-central-1.aws.neon.tech", cs);
        Assert.Contains("Database=neondb", cs);
        Assert.Contains("Username=alex", cs);
        Assert.Contains("Password=p@ss", cs);
        Assert.Contains("SSL Mode=Require", cs);
    }

    [Fact]
    public void Webhook_signature_round_trips()
    {
        var sig = Signature.Sign("secret", "{\"a\":1}");
        Assert.True(Signature.Valid("secret", "{\"a\":1}", sig));
        Assert.False(Signature.Valid("secret", "{\"a\":2}", sig));
    }

    static IConfiguration Config(string secret) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SESSION_SECRET"] = secret }).Build();

    [Fact]
    public void Session_token_round_trips_with_organisation_and_role()
    {
        var token = Tokens.Issue(Config("s1"), Portals.Find("riverside")!);
        var s = Tokens.Read(Config("s1"), token);
        Assert.NotNull(s);
        Assert.Equal(Seeder.Riverside, s!.OrgCode);
        Assert.Equal(Role.Hospital, s.Role);
    }

    [Fact]
    public void Tampered_or_foreign_tokens_are_refused()
    {
        var token = Tokens.Issue(Config("s1"), Portals.Find("palmgrove")!);
        Assert.Null(Tokens.Read(Config("other-secret"), token));
        var forged = Tokens.Issue(Config("s1"), Portals.Find("switch")!).Split('.')[0] + "." + token.Split('.')[1];
        Assert.Null(Tokens.Read(Config("s1"), forged));
        Assert.Null(Tokens.Read(Config("s1"), "not-a-token"));
    }

    [Fact]
    public void Every_portal_has_its_own_login_and_organisation()
    {
        Assert.Equal(6, Portals.All.Select(p => p.Slug).Distinct().Count());
        Assert.Equal(6, Portals.All.Select(p => p.UserName).Distinct().Count());
        Assert.Equal(6, Portals.All.Select(p => p.OrgCode).Distinct().Count());
    }
}
