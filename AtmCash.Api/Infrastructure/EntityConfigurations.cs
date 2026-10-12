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
        b.HasMany(x => x.Holdings).WithOne().HasForeignKey(h => h.CassetteId).OnDelete(DeleteBehavior.Cascade);
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
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
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

public class DepositHoldingConfiguration : IEntityTypeConfiguration<DepositHolding>
{
    public void Configure(EntityTypeBuilder<DepositHolding> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
        b.HasIndex(x => new { x.CassetteId, x.Currency, x.Denomination }).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_DepositHoldings_Count", "\"Count\" >= 0"));
    }
}

public class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> b)
    {
        b.HasKey(x => x.Currency);
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
        b.Property(x => x.RateToTry).HasPrecision(18, 6);
        // Başlangıç kurları (seed). Gerçek hayatta bir kur servisi güncellerdi; ödev için sabit.
        b.HasData(
            new ExchangeRate { Currency = Currency.TRY, RateToTry = 1m },
            new ExchangeRate { Currency = Currency.USD, RateToTry = 42.00m },
            new ExchangeRate { Currency = Currency.EUR, RateToTry = 49.00m });
    }
}

public class ReconciliationConfiguration : IEntityTypeConfiguration<Reconciliation>
{
    public void Configure(EntityTypeBuilder<Reconciliation> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.StartedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.ApprovedBy).HasMaxLength(100);
        b.Property(x => x.TotalAbsoluteDifferenceTry).HasPrecision(18, 2);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ReconciliationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Atm>().WithMany().HasForeignKey(x => x.AtmId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AtmId, x.Status });
    }
}

public class ReconciliationLineConfiguration : IEntityTypeConfiguration<ReconciliationLine>
{
    public void Configure(EntityTypeBuilder<ReconciliationLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Currency).HasConversion<string>().HasMaxLength(3);
        b.Property(x => x.ValueTry).HasPrecision(18, 2);
        b.Ignore(x => x.Difference);
        b.HasOne<Cassette>().WithMany().HasForeignKey(x => x.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(50).IsRequired();
        b.Property(x => x.Payload).IsRequired();
        b.HasIndex(x => new { x.SentAt, x.CreatedAt });
        b.HasIndex(x => x.AtmId);
    }
}
