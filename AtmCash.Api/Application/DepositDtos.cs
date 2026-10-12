using System.ComponentModel.DataAnnotations;
using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record DepositNote([Required] Currency? Currency, int Denomination, int Count);

/// <param name="Notes">Makinenin tanıdığı notlar (para birimi + nominal + adet).</param>
/// <param name="UnrecognizedCount">Makinenin tanıyamadığı not sayısı; işleme dahil edilmez, müşteriye iade edilir.</param>
public record DepositRequest([Required] List<DepositNote> Notes, [Range(0, 100_000)] int UnrecognizedCount = 0);

/// <param name="Reason">MATCHING_CASSETTE | NO_MATCHING_CASSETTE | OVERFLOW</param>
public record DepositPlacementDto(Currency Currency, int Denomination, int Count, int CassetteSlot, CassetteType CassetteType, string Reason);

/// <param name="Reason">DEPOSIT_CASSETTE_FULL | UNRECOGNISED (UNRECOGNISED satırında Currency/Denomination boştur)</param>
public record ReturnedNoteDto(Currency? Currency, int? Denomination, int Count, string Reason);

public record DepositResponse(
    Guid AtmId,
    List<DepositPlacementDto> Accepted,
    List<ReturnedNoteDto> Returned,
    List<CurrencyTotalDto> AcceptedTotals);
