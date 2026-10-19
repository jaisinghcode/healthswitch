namespace HealthSwitch.Domain;

public enum OrgType { Hospital, Hmo, StateScheme, Switch, Processor }

public enum ConnectionMode { Portal, Api }

/// <summary>A participant on the switch: hospital, payer, or an internal party.</summary>
public class Organisation
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public OrgType Type { get; set; }
    public ConnectionMode Mode { get; set; }
    public string State { get; set; } = "";
    /// <summary>For API-connected payers: whether their system is answering (demo control).</summary>
    public bool Online { get; set; } = true;
}

/// <summary>An enrolee as known from a payer's member list.</summary>
public class Member
{
    public string MemberNo { get; set; } = "";
    public string Nin { get; set; } = "";
    public string Name { get; set; } = "";
    public string PayerCode { get; set; } = "";
    public string Plan { get; set; } = "";
    public bool Active { get; set; }
    public DateOnly? ExpiredOn { get; set; }
}

public class ServiceItem
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Agreed tariff in naira.</summary>
    public long Tariff { get; set; }
}

public static class Stages
{
    public const string PreAuth = "preauth";
    public const string FinalClaim = "claim";
    public const string Final = "final";
    public const string ClaimDeclined = "claim-declined";
}

public static class ItemStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Declined = "declined";
    public const string Query = "query";
}

public class Claim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AuthNo { get; set; } = "";
    public string HospitalCode { get; set; } = "";
    public string PayerCode { get; set; } = "";
    public string MemberNo { get; set; } = "";
    public string Nin { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string DiagnosisCode { get; set; } = "";
    public string DiagnosisText { get; set; } = "";
    public string ConsentRef { get; set; } = "";
    public string Stage { get; set; } = Stages.PreAuth;
    /// <summary>Delivery state of the latest hospital-to-payer message: sending, queued, retrying, delivered, dead.</summary>
    public string Delivery { get; set; } = "delivered";
    public bool AwaitingPayer { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; } = Guid.NewGuid();
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public bool IsSample { get; set; }
    public List<LineItem> Items { get; set; } = new();
    public List<Remark> Remarks { get; set; } = new();
    public List<ClaimEvent> Events { get; set; } = new();

    public void Log(string text) => Events.Add(new ClaimEvent { ClaimId = Id, Text = text, At = DateTimeOffset.UtcNow });
}

public class LineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public int Seq { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long Requested { get; set; }
    public long? Approved { get; set; }
    public string Status { get; set; } = ItemStatus.Pending;
    public string Reason { get; set; } = "";
    public string DocType { get; set; } = "";
    public string Note { get; set; } = "";
    public string Response { get; set; } = "";
}

public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LineItemId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Remark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public string By { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class ClaimEvent
{
    public long Id { get; set; }
    public Guid ClaimId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public string AuthNo { get; set; } = "";
    public string HospitalCode { get; set; } = "";
    public string PayerCode { get; set; } = "";
    public string PatientName { get; set; } = "";
    public long Amount { get; set; }
    /// <summary>unpaid, invoiced, paid, disputed</summary>
    public string Status { get; set; } = "unpaid";
    public string? BatchNo { get; set; }
    public DateTimeOffset FinalizedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
    public bool IsSample { get; set; }
}

public class PaymentBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string BatchNo { get; set; } = "";
    public string PayerCode { get; set; } = "";
    public string HospitalCode { get; set; } = "";
    public long Total { get; set; }
    public string VirtualAccount { get; set; } = "";
    /// <summary>pending, confirming, paid, expired, exception</summary>
    public string Status { get; set; } = "pending";
    public string AuthNosCsv { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>Every message that passes through the switch, with its delivery state.</summary>
public class MessageEnvelope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MessageNo { get; set; } = "";
    public string Api { get; set; } = "";
    public string SenderCode { get; set; } = "";
    public string RecipientCode { get; set; } = "";
    public Guid ApiCallId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid? ClaimId { get; set; }
    /// <summary>Whether this message's delivery state is mirrored on the claim (hospital-to-payer requests).</summary>
    public bool Track { get; set; }
    /// <summary>accepted, queued, dispatched, retrying, delivered, dead</summary>
    public string Status { get; set; } = "accepted";
    /// <summary>gateway, q.route, q.deliver.api, q.deliver.portal, q.retry, q.dead, done</summary>
    public string Stage { get; set; } = "gateway";
    public int Attempts { get; set; }
    public string Payload { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EligibilityCheck
{
    public Guid CorrelationId { get; set; }
    public string HospitalCode { get; set; } = "";
    public string MemberNo { get; set; } = "";
    /// <summary>waiting, active, inactive</summary>
    public string Status { get; set; } = "waiting";
    public string Via { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
