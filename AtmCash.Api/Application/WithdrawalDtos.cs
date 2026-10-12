using System.ComponentModel.DataAnnotations;
using AtmCash.Api.Domain;

namespace AtmCash.Api.Application;

public record CreateQuoteRequest(
    [Required, RegularExpression(@"^\d{12,19}$", ErrorMessage = "Card number must be 12-19 digits.")] string CardNumber,
    [Required] Currency? Currency,
    [Range(1, 1_000_000)] int Amount);

public record ConfirmQuoteRequest(QuoteOptionType? Option);

public record QuoteNoteDto(int Denomination, int Count);
public record QuoteOptionDto(QuoteOptionType Option, int Amount, int TotalNotes, List<QuoteNoteDto> Notes);

public record QuoteResponse(
    Guid QuoteId, Guid AtmId, QuoteStatus Status, Currency Currency, int RequestedAmount,
    bool ExactMatch, DateTime ExpiresAt, List<QuoteOptionDto> Options);

public record ConfirmResponse(
    Guid QuoteId, Guid AtmId, QuoteStatus Status, Currency Currency,
    QuoteOptionType Option, int DispensedAmount, List<QuoteNoteDto> Notes);
