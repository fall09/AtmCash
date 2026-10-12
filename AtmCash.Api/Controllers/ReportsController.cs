using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class ReportsController(ReportService reports) : ControllerBase
{
    /// <summary>Operasyon raporu. atmId verilirse tek ATM, verilmezse tüm ağ.</summary>
    [HttpGet("api/reports/operations")]
    public Task<OperationsReport> Operations([FromQuery] Guid? atmId, CancellationToken ct) =>
        reports.GetOperationsReportAsync(atmId, ct);

    /// <summary>Outbox'a yazılan olaylar (düşük nakit uyarıları). sentAt dolu ise arka plan servisi işlemiştir.</summary>
    [HttpGet("api/alerts")]
    public Task<List<AlertDto>> Alerts([FromQuery] Guid? atmId, [FromQuery] int take = 100, CancellationToken ct = default) =>
        reports.GetAlertsAsync(atmId, take, ct);
}
