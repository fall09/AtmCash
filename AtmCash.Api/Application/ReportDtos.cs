using System.Text.Json;
using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record ReportCassetteDto(
    int Slot, CassetteType Type, Currency? Currency, int? Denomination, int Capacity,
    int NoteCount, int ReservedCount, int AvailableCount, bool LowCash,
    List<DenominationTotalDto>? Contents);

public record ReportCurrencyTotalDto(Currency Currency, int NoteCount, long Amount, decimal AmountTry);

public record ReportAtmDto(
    Guid AtmId, string Name, AtmStatus Status,
    List<ReportCassetteDto> Cassettes, List<ReportCurrencyTotalDto> TotalsByCurrency, decimal TotalTry);

public record OperationsReport(
    DateTime GeneratedAt, List<ReportAtmDto> Atms,
    List<ReportCurrencyTotalDto> TotalsByCurrency, decimal OverallTotalTry);

public record AlertDto(Guid Id, string Type, Guid? AtmId, DateTime CreatedAt, DateTime? SentAt, JsonElement Payload);
