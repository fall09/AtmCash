using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

public class AtmService(AppDbContext db, TimeProvider clock)
{
    // ---- Yaşam döngüsü: otomatik geçişler configure/load sonucu olur, elle yapılabilenler burada ----
    private static readonly Dictionary<AtmStatus, AtmStatus[]> ManualTransitions = new()
    {
        [AtmStatus.Loaded] = [AtmStatus.InService],
        [AtmStatus.InService] = [AtmStatus.Maintenance],
        [AtmStatus.Maintenance] = [AtmStatus.InService],
    };

    public async Task<AtmDto> CreateAsync(string name, CancellationToken ct)
    {
        var atm = new Atm
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Status = AtmStatus.Unconfigured,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        atm.Cassettes.Add(new Cassette { Id = Guid.NewGuid(), Slot = 1, Type = CassetteType.Deposit });
        for (var slot = 2; slot <= 5; slot++)
            atm.Cassettes.Add(new Cassette { Id = Guid.NewGuid(), Slot = slot, Type = CassetteType.Recycling });

        db.Atms.Add(atm);
        await db.SaveChangesAsync(ct);
        return ToDto(atm);
    }

    public async Task<List<AtmDto>> ListAsync(CancellationToken ct)
    {
        var atms = await db.Atms.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return atms.Select(ToDto).ToList();
    }

    public async Task<AtmInventoryDto> GetInventoryAsync(Guid id, CancellationToken ct)
    {
        var atm = await db.Atms.AsNoTracking().Include(a => a.Cassettes).ThenInclude(c => c.Holdings)
                      .SingleOrDefaultAsync(a => a.Id == id, ct)
                  ?? throw AtmLocking.AtmNotFound(id);
        return ToInventory(atm);
    }

    public async Task<AtmInventoryDto> ConfigureCassettesAsync(Guid id, ConfigureCassettesRequest req, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(id, ct);
        atm.EnsureState("configure cassettes", AtmStatus.Unconfigured, AtmStatus.Configured, AtmStatus.Maintenance);

        var items = req.Cassettes;
        if (items.Count != 4 || items.Select(i => i.Slot).Distinct().Order().SequenceEqual(new[] { 2, 3, 4, 5 }) is false)
            throw Invalid("INVALID_CASSETTE_CONFIGURATION", "Exactly the 4 recycling cassettes (slots 2, 3, 4, 5) must be configured, once each.");

        foreach (var i in items)
            if (!Denominations.Allowed.Contains(i.Denomination))
                throw Invalid("INVALID_CASSETTE_CONFIGURATION", $"Denomination {i.Denomination} is not supported. Allowed: {string.Join(", ", Denominations.Allowed)}.");

        var foreign = items.Count(i => i.Currency != Currency.TRY);
        if (foreign is < 1 or > 2)
            throw Invalid("INVALID_CASSETTE_CONFIGURATION", $"At least 1 and at most 2 recycling cassettes must be foreign currency (USD/EUR); got {foreign}.");

        foreach (var i in items)
        {
            var cassette = atm.Cassettes.Single(c => c.Slot == i.Slot);
            var changes = cassette.Currency != i.Currency || cassette.Denomination != i.Denomination;
            if (changes && cassette.Count > 0)
                throw Invalid("CASSETTE_NOT_EMPTY", $"Cassette {i.Slot} still holds {cassette.Count} notes; unload it before changing its currency/denomination.");
            cassette.Currency = i.Currency;
            cassette.Denomination = i.Denomination;
        }

        if (atm.Status == AtmStatus.Unconfigured) atm.Status = AtmStatus.Configured;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToInventory(atm);
    }

    public async Task<AtmInventoryDto> LoadCashAsync(Guid id, LoadCashRequest req, string? user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(id, ct);
        atm.EnsureState("load cash", AtmStatus.Configured, AtmStatus.Maintenance);

        if (req.Cassettes.Count == 0)
            throw Invalid("INVALID_LOAD", "At least one cassette must be loaded.");
        if (req.Cassettes.Select(c => c.Slot).Distinct().Count() != req.Cassettes.Count)
            throw Invalid("INVALID_LOAD", "Each cassette slot may appear only once.");

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var item in req.Cassettes)
        {
            var cassette = atm.Cassettes.SingleOrDefault(c => c.Slot == item.Slot)
                           ?? throw Invalid("INVALID_LOAD", $"Cassette slot {item.Slot} does not exist.");
            if (cassette.Type != CassetteType.Recycling)
                throw Invalid("INVALID_LOAD", "Cash can only be loaded by hand into recycling cassettes (slots 2-5).");
            if (!cassette.IsConfigured)
                throw Invalid("CASSETTES_NOT_CONFIGURED", $"Cassette {item.Slot} has no currency/denomination configured.", StatusCodes.Status409Conflict);
            if (item.Count <= 0)
                throw Invalid("INVALID_LOAD", "Load count must be positive.");
            if (cassette.Count + item.Count > cassette.Capacity)
                throw Invalid("CASSETTE_CAPACITY_EXCEEDED", $"Cassette {item.Slot} holds {cassette.Count} of {cassette.Capacity} notes; cannot add {item.Count}.");

            cassette.Count += item.Count;
            db.CassetteMovements.Add(new CassetteMovement
            {
                Id = Guid.NewGuid(), AtmId = atm.Id, CassetteId = cassette.Id,
                Type = MovementType.Load, CountDelta = item.Count, ReservedDelta = 0,
                CreatedAt = now, CreatedBy = user,
            });
        }

        if (atm.Status == AtmStatus.Configured && atm.Cassettes.Any(c => c.Count > 0))
            atm.Status = AtmStatus.Loaded;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToInventory(atm);
    }

    public async Task<AtmInventoryDto> TransitionAsync(Guid id, AtmStatus target, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(id, ct);

        if (!ManualTransitions.TryGetValue(atm.Status, out var allowed) || !allowed.Contains(target))
        {
            var hint = atm.Status switch
            {
                AtmStatus.Unconfigured => " Configure the cassettes first (UNCONFIGURED -> CONFIGURED happens automatically).",
                AtmStatus.Configured => " Load cash first (CONFIGURED -> LOADED happens automatically).",
                _ => "",
            };
            throw new DomainException("INVALID_STATE_TRANSITION",
                $"Cannot move ATM from {Atm.StatusName(atm.Status)} to {Atm.StatusName(target)}.{hint}",
                StatusCodes.Status409Conflict);
        }

        if (target == AtmStatus.Maintenance && atm.Cassettes.Any(c => c.ReservedCount > 0))
            throw new DomainException("ATM_HAS_ACTIVE_RESERVATIONS",
                "ATM has open withdrawal quotes; wait for them to be confirmed, cancelled or expired.",
                StatusCodes.Status409Conflict);

        atm.Status = target;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToInventory(atm);
    }

    // ---- yardımcılar ----
    private static DomainException Invalid(string code, string message, int status = StatusCodes.Status422UnprocessableEntity) =>
        new(code, message, status);

    private static AtmDto ToDto(Atm a) => new(a.Id, a.Name, a.Status, a.CreatedAt);

    /// <summary>Notes/Totals fiziksel içeriktir: geri dönüşümlü kasetler + deposit kasetinin içindekiler.</summary>
    public static AtmInventoryDto ToInventory(Atm atm)
    {
        var cassettes = atm.Cassettes.OrderBy(c => c.Slot)
            .Select(c => new CassetteDto(c.Slot, c.Type, c.Currency, c.Denomination, c.Capacity, c.Count, c.ReservedCount, c.AvailableCount,
                c.Type == CassetteType.Deposit
                    ? c.Holdings.Where(h => h.Count > 0).OrderBy(h => h.Currency).ThenBy(h => h.Denomination)
                        .Select(h => new DenominationTotalDto(h.Currency, h.Denomination, h.Count)).ToList()
                    : null))
            .ToList();

        var physical = atm.Cassettes
            .Where(c => c.Type == CassetteType.Recycling && c.IsConfigured)
            .Select(c => (Currency: c.Currency!.Value, Denom: c.Denomination!.Value, Count: c.Count))
            .Concat(atm.Cassettes.SelectMany(c => c.Holdings).Where(h => h.Count > 0)
                .Select(h => (Currency: h.Currency, Denom: h.Denomination, Count: h.Count)))
            .ToList();

        var notes = physical
            .GroupBy(x => (x.Currency, x.Denom))
            .Select(g => new DenominationTotalDto(g.Key.Currency, g.Key.Denom, g.Sum(x => x.Count)))
            .OrderBy(n => n.Currency).ThenBy(n => n.Denomination)
            .ToList();

        var totals = physical
            .GroupBy(x => x.Currency)
            .Select(g => new CurrencyTotalDto(g.Key, g.Sum(x => (long)x.Count * x.Denom)))
            .OrderBy(t => t.Currency)
            .ToList();

        return new AtmInventoryDto(atm.Id, atm.Name, atm.Status, cassettes, notes, totals);
    }
}
