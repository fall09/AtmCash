using System.Data;
using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

/// <summary>
/// B8: Kaset hareket defterini okur ve saklanan bakiyeleri (Count, ReservedCount, deposit içeriği)
/// hareketlerin toplamından yeniden hesaplanan değerlerle karşılaştırır.
/// </summary>
public class LedgerService(AppDbContext db, TimeProvider clock)
{
    public async Task<List<MovementDto>> GetMovementsAsync(Guid atmId, int? slot, int take, CancellationToken ct)
    {
        if (!await db.Atms.AnyAsync(a => a.Id == atmId, ct)) throw AtmLocking.AtmNotFound(atmId);
        take = Math.Clamp(take, 1, 500);

        var query = from m in db.CassetteMovements.AsNoTracking()
                    join c in db.Cassettes.AsNoTracking() on m.CassetteId equals c.Id
                    where m.AtmId == atmId
                    select new { Movement = m, c.Slot };
        if (slot is not null) query = query.Where(x => x.Slot == slot);

        var rows = await query.OrderByDescending(x => x.Movement.CreatedAt).Take(take).ToListAsync(ct);
        return rows.Select(x => new MovementDto(
            x.Movement.Id, x.Slot, x.Movement.Type, x.Movement.CountDelta, x.Movement.ReservedDelta,
            x.Movement.Currency, x.Movement.Denomination, x.Movement.CreatedAt, x.Movement.Reference, x.Movement.CreatedBy)).ToList();
    }

    public async Task<ConsistencyReport> CheckConsistencyAsync(Guid atmId, CancellationToken ct)
    {
        // Tutarlı bir anlık görüntü: kasetler ve hareketler aynı snapshot'tan okunur (işlemler sürerken yanlış alarm olmasın).
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var atm = await db.Atms.AsNoTracking().Include(a => a.Cassettes).ThenInclude(c => c.Holdings)
                      .SingleOrDefaultAsync(a => a.Id == atmId, ct)
                  ?? throw AtmLocking.AtmNotFound(atmId);

        var sums = await db.CassetteMovements.AsNoTracking()
            .Where(m => m.AtmId == atmId)
            .GroupBy(m => m.CassetteId)
            .Select(g => new { CassetteId = g.Key, Count = g.Sum(m => m.CountDelta), Reserved = g.Sum(m => m.ReservedDelta) })
            .ToListAsync(ct);

        var denomSums = (await db.CassetteMovements.AsNoTracking()
                .Where(m => m.AtmId == atmId && m.Currency != null)
                .GroupBy(m => new { m.CassetteId, m.Currency, m.Denomination })
                .Select(g => new { g.Key.CassetteId, g.Key.Currency, g.Key.Denomination, Count = g.Sum(m => m.CountDelta) })
                .ToListAsync(ct))
            .Select(x => (x.CassetteId, Currency: x.Currency!.Value, Denomination: x.Denomination!.Value, x.Count))
            .ToList();

        await tx.CommitAsync(ct);

        var differences = new List<string>();
        var cassettes = new List<CassetteConsistencyDto>();

        foreach (var c in atm.Cassettes.OrderBy(c => c.Slot))
        {
            var s = sums.FirstOrDefault(x => x.CassetteId == c.Id);
            var ledgerCount = s?.Count ?? 0;
            var ledgerReserved = s?.Reserved ?? 0;
            var ok = c.Count == ledgerCount && c.ReservedCount == ledgerReserved;

            if (c.Count != ledgerCount)
                differences.Add($"Cassette {c.Slot}: stored count {c.Count} but ledger sums to {ledgerCount} (difference {c.Count - ledgerCount}).");
            if (c.ReservedCount != ledgerReserved)
                differences.Add($"Cassette {c.Slot}: stored reserved {c.ReservedCount} but ledger sums to {ledgerReserved} (difference {c.ReservedCount - ledgerReserved}).");

            List<HoldingConsistencyDto>? holdings = null;
            if (c.Type == CassetteType.Deposit)
            {
                var keys = c.Holdings.Select(h => (h.Currency, h.Denomination))
                    .Union(denomSums.Where(d => d.CassetteId == c.Id).Select(d => (d.Currency, d.Denomination)))
                    .OrderBy(k => k.Currency).ThenBy(k => k.Denomination);

                holdings = [];
                foreach (var (currency, denomination) in keys)
                {
                    var stored = c.Holdings.FirstOrDefault(h => h.Currency == currency && h.Denomination == denomination)?.Count ?? 0;
                    var fromLedger = denomSums.Where(d => d.CassetteId == c.Id && d.Currency == currency && d.Denomination == denomination)
                        .Sum(d => d.Count);
                    var holdingOk = stored == fromLedger;
                    ok &= holdingOk;
                    if (!holdingOk)
                        differences.Add($"Deposit cassette {currency} {denomination}: stored {stored} but ledger sums to {fromLedger}.");
                    holdings.Add(new HoldingConsistencyDto(currency, denomination, stored, fromLedger, holdingOk));
                }
            }

            cassettes.Add(new CassetteConsistencyDto(c.Slot, c.Type, c.Count, ledgerCount, c.ReservedCount, ledgerReserved, ok, holdings));
        }

        return new ConsistencyReport(atm.Id, clock.GetUtcNow().UtcDateTime, differences.Count == 0, cassettes, differences);
    }
}
