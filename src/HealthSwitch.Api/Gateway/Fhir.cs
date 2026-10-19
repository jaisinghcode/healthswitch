using System.Text.Json;
using HealthSwitch.Domain;

namespace HealthSwitch.Gateway;

/// <summary>
/// Builds simplified FHIR R4 payloads shaped on the NHCX bundle profiles.
/// Version 1 sends these as readable JSON; payload encryption (JWE) is a later step.
/// </summary>
public static class Fhir
{
    static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ClaimBundle(Claim c, string hospitalName, string payerName, string payerCode, string use) =>
        JsonSerializer.Serialize(new
        {
            resourceType = "Bundle",
            type = "collection",
            meta = new { profile = new[] { "https://nrces.in/ndhm/fhir/r4/StructureDefinition/ClaimBundle" } },
            timestamp = DateTimeOffset.UtcNow,
            entry = new object[]
            {
                new
                {
                    resource = new
                    {
                        resourceType = "Claim",
                        use,
                        status = "active",
                        identifier = new[] { new { system = "https://healthswitch.ng/id/claim", value = c.AuthNo } },
                        patient = new
                        {
                            identifier = new[]
                            {
                                new { system = "https://healthswitch.ng/id/nin", value = c.Nin },
                                new { system = "https://healthswitch.ng/id/member/" + payerCode, value = c.MemberNo },
                            },
                            display = c.PatientName,
                        },
                        provider = new { display = hospitalName },
                        insurer = new { display = payerName },
                        diagnosis = new[] { new { sequence = 1, diagnosisCodeableConcept = new { coding = new[] { new { system = "http://hl7.org/fhir/sid/icd-10", code = c.DiagnosisCode, display = c.DiagnosisText } } } } },
                        item = c.Items.OrderBy(i => i.Seq).Select(i => new
                        {
                            sequence = i.Seq,
                            productOrService = new { coding = new[] { new { system = "https://healthswitch.ng/tariff", code = i.Code, display = i.Name } } },
                            net = new { value = use == "claim" ? (i.Approved ?? 0) : i.Requested, currency = "NGN" },
                        }),
                        supportingInfo = new[] { new { sequence = 1, category = new { text = "consent" }, valueString = c.ConsentRef } },
                    },
                },
            },
        }, Opts);

    public static string Communication(Claim c, IEnumerable<Attachment> files, string note) =>
        JsonSerializer.Serialize(new
        {
            resourceType = "Communication",
            status = "completed",
            about = new[] { new { identifier = new { value = c.AuthNo } } },
            payload = files.Select(f => (object)new { contentAttachment = new { contentType = f.ContentType, title = f.FileName, size = f.Size, url = "attachments/" + f.Id } })
                .Concat(string.IsNullOrWhiteSpace(note) ? Array.Empty<object>() : new object[] { new { contentString = note } }),
        }, Opts);

    public static string Json(object o) => JsonSerializer.Serialize(o, Opts);
    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Opts);
}

public record DecisionDto(Guid LineId, string Status, long? Approved, string? Reason, string? DocType, string? Note);
public record RemarkDto(string By, string Text);
public record DecisionPayload(List<DecisionDto> Decisions, RemarkDto? Remark);
public record OutcomePayload(string Outcome);
public record EligibilityQuery(string MemberNo, string? Nin);
public record EligibilityAnswer(string MemberNo, bool Active, string Via);
public record BatchPayload(string BatchNo, long Amount);
