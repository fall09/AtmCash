using System.ComponentModel.DataAnnotations;
using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record ReconcileCount(int Slot, int CountedCount);
public record ReconcileDepositCount([Required] Currency? Currency, int Denomination, int CountedCount);

/// <param name="Counts">Her geri dönüşümlü kaset (slot 2-5) için elle sayılan not adedi.</param>
/// <param name="DepositCounts">Opsiyonel: deposit kasetinin tam sayımı. Verilirse, listede olmayan nominaller 0 sayılmış kabul edilir.</param>
public record ReconcileRequest([Required] List<ReconcileCount> Counts, List<ReconcileDepositCount>? DepositCounts = null);

public record ReconciliationLineDto(
    int Slot, Currency? Currency, int? Denomination,
    int SystemCount, int CountedCount, int Difference, decimal DifferenceValueTry);

public record ReconciliationDto(
    Guid Id, Guid AtmId, ReconciliationStatus Status, string StartedBy, DateTime CreatedAt,
    decimal TotalAbsoluteDifferenceTry, decimal ApprovalThresholdTry,
    string? ApprovedBy, DateTime? ApprovedAt, List<ReconciliationLineDto> Lines);

public record ExchangeRateDto(Currency Currency, decimal RateToTry);
