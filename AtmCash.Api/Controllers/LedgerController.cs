using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class LedgerController(LedgerService ledger) : ControllerBase
{
    /// <summary>Kaset hareket defteri (en yeni önce). slot ile tek bir kasete süzülebilir.</summary>
    [HttpGet("api/atms/{atmId:guid}/movements")]
    public Task<List<MovementDto>> Movements(Guid atmId, [FromQuery] int? slot, [FromQuery] int take = 100, CancellationToken ct = default) =>
        ledger.GetMovementsAsync(atmId, slot, take, ct);

    /// <summary>Saklanan bakiyeleri hareketlerin toplamından hesaplananlarla karşılaştırır ve farkları raporlar.</summary>
    [HttpGet("api/atms/{atmId:guid}/ledger/consistency")]
    public Task<ConsistencyReport> Consistency(Guid atmId, CancellationToken ct) => ledger.CheckConsistencyAsync(atmId, ct);
}
