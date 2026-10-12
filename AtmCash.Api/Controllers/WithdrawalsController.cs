using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class WithdrawalsController(WithdrawalService withdrawals) : ControllerBase
{
    /// <summary>
    /// 1. adım: tutar iste. Tam eşleşme varsa tek seçenek, yoksa alt/üst alternatifler döner ve sunulan notlar 60 saniye rezerve edilir.
    /// Kart günlük limiti burada kontrol edilir (bekleyen quote'lar dahil).
    /// </summary>
    [HttpPost("api/atms/{atmId:guid}/withdrawals/quotes")]
    [ProducesResponseType<QuoteResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateQuote(Guid atmId, CreateQuoteRequest req, CancellationToken ct)
    {
        var quote = await withdrawals.CreateQuoteAsync(atmId, req, ct);
        return CreatedAtAction(nameof(GetQuote), new { quoteId = quote.QuoteId }, quote);
    }

    [HttpGet("api/quotes/{quoteId:guid}")]
    public Task<QuoteResponse> GetQuote(Guid quoteId, CancellationToken ct) => withdrawals.GetQuoteAsync(quoteId, ct);

    /// <summary>
    /// 2. adım: onayla. Idempotency-Key header'ı zorunlu; aynı key ile tekrar gelen istek aynı sonucu döner (nakit bir kez düşer).
    /// Alternatifli quote'larda body'de option (LOWER/UPPER) seçilmelidir.
    /// </summary>
    [HttpPost("api/quotes/{quoteId:guid}/confirm")]
    public async Task<ConfirmResponse> Confirm(Guid quoteId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ConfirmQuoteRequest? req, CancellationToken ct)
    {
        var (response, replayed) = await withdrawals.ConfirmAsync(quoteId, idempotencyKey, req?.Option, ct);
        if (replayed) Response.Headers["Idempotent-Replayed"] = "true";
        return response;
    }

    /// <summary>Quote'u iptal eder ve rezervasyonu serbest bırakır.</summary>
    [HttpPost("api/quotes/{quoteId:guid}/cancel")]
    public Task<QuoteResponse> Cancel(Guid quoteId, CancellationToken ct) => withdrawals.CancelAsync(quoteId, ct);
}
