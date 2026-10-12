using AtmCash.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Api.Infrastructure;

public class AtmConfiguration : IEntityTypeConfiguration<Atm>
{
    public void Configure(EntityTypeBuilder<Atm> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasMany(x => x.Cassettes).WithOne().HasForeignKey(c => c.AtmId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CassetteConfiguration : IEntityTypeConfiguration<Cassette>
{
    public void Configure(EntityTypeBuilder<Cassette> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
        b.Ignore(x => x.AvailableCount);
        b.Ignore(x => x.IsConfigured);
        b.HasIndex(x => new { x.AtmId, x.Slot }).IsUnique();
        // Son savunma hattı: uygulama hatalı olsa bile sayaçlar mantıksız değerlere gidemez (B6).
        b.ToTable(t => t.HasCheckConstraint(
            "CK_Cassettes_Counts",
            "\"Count\" >= 0 AND \"Count\" <= \"Capacity\" AND \"ReservedCount\" >= 0 AND \"ReservedCount\" <= \"Count\""));
    }
}

public class CassetteMovementConfiguration : IEntityTypeConfiguration<CassetteMovement>
{
    public void Configure(EntityTypeBuilder<CassetteMovement> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.CreatedBy).HasMaxLength(100);
        b.HasOne<Cassette>().WithMany().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CassetteId);
        b.HasIndex(x => x.AtmId);
    }
}

public class WithdrawalQuoteConfiguration : IEntityTypeConfiguration<WithdrawalQuote>
{
    public void Configure(EntityTypeBuilder<WithdrawalQuote> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.CardHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ChosenOption).HasConversion<string>().HasMaxLength(10);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CardHash, x.Currency, x.Status });
        b.HasIndex(x => new { x.Status, x.ExpiresAt });
    }
}

public class QuoteLineConfiguration : IEntityTypeConfiguration<QuoteLine>
{
    public void Configure(EntityTypeBuilder<QuoteLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Option).HasConversion<string>().HasMaxLength(10);
        b.HasOne<Cassette>().WithMany().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Option).HasConversion<string>().HasMaxLength(10);
    }
}
