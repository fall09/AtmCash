using AtmCash.Api.Application;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Controllers;

[ApiController]
[Produces("application/json")]
[Route("api/atms")]
public class AtmsController(AtmService atms) : ControllerBase
{
    /// <summary>Yeni ATM oluşturur (5 kaset: 1 deposit + 4 henüz yapılandırılmamış geri dönüşümlü).</summary>
    [HttpPost]
    [ProducesResponseType<AtmDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateAtmRequest req, CancellationToken ct)
    {
        var atm = await atms.CreateAsync(req.Name, ct);
        return CreatedAtAction(nameof(Get), new { id = atm.Id }, atm);
    }

    [HttpGet]
    public Task<List<AtmDto>> List(CancellationToken ct) => atms.ListAsync(ct);

    /// <summary>Operasyon görünümü: kaset kaset not sayıları, nominal bazında toplamlar ve para birimi bakiyeleri.</summary>
    [HttpGet("{id:guid}")]
    public Task<AtmInventoryDto> Get(Guid id, CancellationToken ct) => atms.GetInventoryAsync(id, ct);

    /// <summary>4 geri dönüşümlü kaseti (slot 2-5) yapılandırır. UNCONFIGURED/CONFIGURED/MAINTENANCE durumlarında izinli.</summary>
    [HttpPut("{id:guid}/cassettes")]
    public Task<AtmInventoryDto> Configure(Guid id, ConfigureCassettesRequest req, CancellationToken ct) =>
        atms.ConfigureCassettesAsync(id, req, ct);

    /// <summary>Elle yüklenen nakdi kaydeder (LOAD hareketi yazar). CONFIGURED/MAINTENANCE durumlarında izinli.</summary>
    [HttpPost("{id:guid}/load")]
    public Task<AtmInventoryDto> Load(Guid id, LoadCashRequest req,
        [FromHeader(Name = "X-User")] string? user, CancellationToken ct) =>
        atms.LoadCashAsync(id, req, user, ct);

    /// <summary>Durum geçişi: LOADED→IN_SERVICE, IN_SERVICE⇄MAINTENANCE. Geçersiz geçişler 409 döner.</summary>
    [HttpPost("{id:guid}/transitions")]
    public Task<AtmInventoryDto> Transition(Guid id, TransitionRequest req, CancellationToken ct) =>
        atms.TransitionAsync(id, req.Target, ct);
}
