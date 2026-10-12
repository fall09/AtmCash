using System.ComponentModel.DataAnnotations;
using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record CreateAtmRequest([Required, StringLength(100, MinimumLength = 1)] string Name);

public record CassetteConfigItem(int Slot, Currency Currency, int Denomination);
public record ConfigureCassettesRequest([Required] List<CassetteConfigItem> Cassettes);

public record LoadItem(int Slot, int Count);
public record LoadCashRequest([Required] List<LoadItem> Cassettes);

public record UnloadCashRequest([Required] List<LoadItem> Cassettes);

public record TransitionRequest(AtmStatus Target);

public record AtmDto(Guid Id, string Name, AtmStatus Status, DateTime CreatedAt);

public record CassetteDto(
    int Slot, CassetteType Type, Currency? Currency, int? Denomination,
    int Capacity, int Count, int ReservedCount, int AvailableCount,
    List<DenominationTotalDto>? Contents = null);

public record DenominationTotalDto(Currency Currency, int Denomination, int Count);
public record CurrencyTotalDto(Currency Currency, long Amount);

public record AtmInventoryDto(
    Guid Id, string Name, AtmStatus Status,
    List<CassetteDto> Cassettes,
    List<DenominationTotalDto> Notes,
    List<CurrencyTotalDto> Totals);
