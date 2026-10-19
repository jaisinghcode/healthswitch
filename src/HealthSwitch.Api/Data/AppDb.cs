using HealthSwitch.Domain;
using Microsoft.EntityFrameworkCore;

namespace HealthSwitch.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<ServiceItem> Services => Set<ServiceItem>();
    public DbSet<Claim> Claims => Set<Claim>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Remark> Remarks => Set<Remark>();
    public DbSet<ClaimEvent> ClaimEvents => Set<ClaimEvent>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<PaymentBatch> Batches => Set<PaymentBatch>();
    public DbSet<MessageEnvelope> Envelopes => Set<MessageEnvelope>();
    public DbSet<EligibilityCheck> EligibilityChecks => Set<EligibilityCheck>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasSequence<long>("auth_seq").StartsAt(1043);
        b.HasSequence<long>("msg_seq").StartsAt(1);
        b.HasSequence<long>("batch_seq").StartsAt(7);

        b.Entity<Organisation>(e => { e.HasKey(x => x.Code); e.Property(x => x.Type).HasConversion<string>(); e.Property(x => x.Mode).HasConversion<string>(); });
        b.Entity<Member>(e => { e.HasKey(x => x.MemberNo); e.HasIndex(x => x.Nin); });
        b.Entity<ServiceItem>(e => e.HasKey(x => x.Code));
        b.Entity<Claim>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AuthNo).IsUnique();
            e.HasIndex(x => new { x.PayerCode, x.AwaitingPayer });
            e.HasIndex(x => x.HospitalCode);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Remarks).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<LineItem>(e => e.HasKey(x => x.Id));
        b.Entity<Attachment>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.LineItemId); });
        b.Entity<Remark>(e => e.HasKey(x => x.Id));
        b.Entity<ClaimEvent>(e => e.HasKey(x => x.Id));
        b.Entity<LedgerEntry>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.ClaimId).IsUnique(); e.HasIndex(x => new { x.PayerCode, x.HospitalCode, x.Status }); });
        b.Entity<PaymentBatch>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.BatchNo).IsUnique(); e.HasIndex(x => x.VirtualAccount).IsUnique(); });
        b.Entity<MessageEnvelope>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ApiCallId).IsUnique();
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
        b.Entity<EligibilityCheck>(e => e.HasKey(x => x.CorrelationId));
    }

    /// <summary>Next value of a database sequence (auth_seq, msg_seq, batch_seq).</summary>
    public async Task<long> NextAsync(string sequence, CancellationToken ct = default)
    {
        if (sequence is not ("auth_seq" or "msg_seq" or "batch_seq")) throw new ArgumentException("Unknown sequence", nameof(sequence));
        return await Database.SqlQueryRaw<long>("SELECT nextval('" + sequence + "') AS \"Value\"").SingleAsync(ct);
    }
}
