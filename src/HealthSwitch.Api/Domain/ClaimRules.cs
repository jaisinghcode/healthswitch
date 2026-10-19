namespace HealthSwitch.Domain;

/// <summary>Status roll-up rules from the project scope, section 7.2.</summary>
public static class ClaimRules
{
    /// <summary>Returns pending, query, approved, partial or declined.</summary>
    public static string RollUp(IReadOnlyCollection<LineItem> items)
    {
        if (items.Count == 0) return "pending";
        if (items.Any(i => i.Status == ItemStatus.Query)) return "query";
        if (items.Any(i => i.Status == ItemStatus.Pending)) return "pending";
        if (items.All(i => i.Status == ItemStatus.Declined)) return "declined";
        var reduced = items.Any(i => i.Status == ItemStatus.Approved && (i.Approved ?? 0) < i.Requested);
        if (items.All(i => i.Status == ItemStatus.Approved) && !reduced) return "approved";
        return "partial";
    }

    public static long ApprovedTotal(IEnumerable<LineItem> items) => items.Sum(i => i.Approved ?? 0);

    /// <summary>The label and colour kind the hospital sees.</summary>
    public static (string Label, string Kind) HospitalStatus(Claim c, LedgerEntry? ledger)
    {
        switch (c.Stage)
        {
            case Stages.FinalClaim: return ("Final claim with payer", "info");
            case Stages.ClaimDeclined: return ("Claim declined", "bad");
            case Stages.Final:
                return (ledger?.Status ?? "unpaid") switch
                {
                    "paid" => ("Paid", "paid"),
                    "invoiced" => ("Invoiced", "info"),
                    _ => ("Approved, unpaid", "ok"),
                };
        }
        return RollUp(c.Items) switch
        {
            "query" => ("Query raised", "warn"),
            "approved" => ("Approved", "ok"),
            "partial" => ("Partially approved", "ok"),
            "declined" => ("Declined", "bad"),
            _ => ("Pending", "neutral"),
        };
    }

    /// <summary>Approve each service up to the agreed tariff (the sample rule used by the simulated API payer).</summary>
    public static (string Status, long Approved, string Reason) TariffRule(long requested, long tariff) =>
        requested > tariff ? (ItemStatus.Approved, tariff, "Approved at agreed tariff") : (ItemStatus.Approved, requested, "");
}
