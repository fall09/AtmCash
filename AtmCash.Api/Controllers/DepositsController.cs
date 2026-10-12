using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class DepositsController(DepositService deposits) : ControllerBase
{
    /// <summary>
    /// Para yatırma. Yanıt, her notun hangi kasete gittiğini (accepted) ve müşteriye iade edilenleri (returned) ayrı gösterir.
    /// Sadece IN_SERVICE durumunda izinli.
    /// </summary>
    [HttpPost("api/atms/{atmId:guid}/deposits")]
    public Task<DepositResponse> Deposit(Guid atmId, DepositRequest req,
        [FromHeader(Name = "X-User")] string? user, CancellationToken ct) =>
        deposits.DepositAsync(atmId, req, user, ct);
}
