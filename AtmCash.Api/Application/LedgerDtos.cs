using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record MovementDto(
    Guid Id, int CassetteSlot, MovementType Type, int CountDelta, int ReservedDelta,
    Currency? Currency, int? Denomination, DateTime CreatedAt, string? Reference, string? CreatedBy);

public record HoldingConsistencyDto(Currency Currency, int Denomination, int StoredCount, int LedgerCount, bool Consistent);

public record CassetteConsistencyDto(
    int Slot, CassetteType Type,
    int StoredCount, int LedgerCount, int StoredReserved, int LedgerReserved,
    bool Consistent, List<HoldingConsistencyDto>? Holdings);

public record ConsistencyReport(
    Guid AtmId, DateTime CheckedAt, bool Consistent,
    List<CassetteConsistencyDto> Cassettes, List<string> Differences);
