using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class ReconciliationsController(ReconciliationService reconciliations) : ControllerBase
{
    /// <summary>
    /// Elle sayım sonucunu girer (sadece MAINTENANCE). Fark hemen RECONCILIATION_ADJUSTMENT olarak uygulanır.
    /// Farkların toplam TRY değeri 1.000'i aşarsa durum AWAITING_APPROVAL olur. X-User header'ı zorunlu.
    /// </summary>
    [HttpPost("api/atms/{atmId:guid}/reconciliations")]
    [ProducesResponseType<ReconciliationDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Reconcile(Guid atmId, ReconcileRequest req,
        [FromHeader(Name = "X-User")] string? user, CancellationToken ct)
    {
        var result = await reconciliations.ReconcileAsync(atmId, req, user, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    [HttpGet("api/atms/{atmId:guid}/reconciliations")]
    public Task<List<ReconciliationDto>> List(Guid atmId, CancellationToken ct) => reconciliations.ListAsync(atmId, ct);

    [HttpGet("api/reconciliations/{id:guid}")]
    public Task<ReconciliationDto> Get(Guid id, CancellationToken ct) => reconciliations.GetAsync(id, ct);

    /// <summary>Dört göz onayı: mutabakatı başlatandan FARKLI bir X-User ile onaylanmalıdır.</summary>
    [HttpPost("api/reconciliations/{id:guid}/approve")]
    public Task<ReconciliationDto> Approve(Guid id, [FromHeader(Name = "X-User")] string? user, CancellationToken ct) =>
        reconciliations.ApproveAsync(id, user, ct);

    /// <summary>TRY karşılığı hesabında kullanılan döviz kuru tablosu (seed verisi).</summary>
    [HttpGet("api/exchange-rates")]
    public Task<List<ExchangeRateDto>> Rates(CancellationToken ct) => reconciliations.GetRatesAsync(ct);
}
