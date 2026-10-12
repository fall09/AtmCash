using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;

namespace AtmCash.Api.Application;

/// <summary>
/// B7: Para yatırma. Her not önce eşleşen geri dönüşümlü kasete gider; kaset dolarsa taşan kısım
/// ya da eşleşmeyen notlar deposit kasetine; o da doluysa müşteriye iade edilir.
/// </summary>
public class DepositService(AppDbContext db, TimeProvider clock)
{
    public async Task<DepositResponse> DepositAsync(Guid atmId, DepositRequest req, string? user, CancellationToken ct)
    {
        foreach (var n in req.Notes)
        {
            if (!Denominations.Allowed.Contains(n.Denomination))
                throw Invalid($"Denomination {n.Denomination} is not supported. Allowed: {string.Join(", ", Denominations.Allowed)}.");
            if (n.Count <= 0)
                throw Invalid("Note counts must be positive.");
        }

        if (req.Notes.Count == 0 && req.UnrecognizedCount == 0)
            throw Invalid("The deposit contains no notes.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
        atm.EnsureState("deposit", AtmStatus.InService);

        var now = clock.GetUtcNow().UtcDateTime;
        var reference = $"deposit:{Guid.NewGuid()}";
        var depositCassette = atm.Cassettes.Single(c => c.Type == CassetteType.Deposit);

        var accepted = new List<DepositPlacementDto>();
        var returned = new List<ReturnedNoteDto>();

        var groups = req.Notes
            .GroupBy(n => (Currency: n.Currency!.Value, n.Denomination))
            .OrderBy(g => g.Key.Currency).ThenBy(g => g.Key.Denomination);

        foreach (var g in groups)
        {
            var (currency, denomination) = g.Key;
            var remaining = g.Sum(n => n.Count);

            // 1) Eşleşen geri dönüşümlü kasetler: en boş olandan başlayarak doldur.
            var matching = atm.Cassettes
                .Where(c => c.Type == CassetteType.Recycling && c.Currency == currency && c.Denomination == denomination)
                .OrderByDescending(c => c.Capacity - c.Count).ThenBy(c => c.Slot)
                .ToList();

            foreach (var c in matching)
            {
                var take = Math.Min(remaining, c.Capacity - c.Count);
                if (take <= 0) continue;
                c.Count += take;
                remaining -= take;
                db.CassetteMovements.Add(Movement(atm.Id, c.Id, take, currency, denomination, now, reference, user));
                accepted.Add(new DepositPlacementDto(currency, denomination, take, c.Slot, CassetteType.Recycling, "MATCHING_CASSETTE"));
            }

            // 2) Eşleşme yok ya da kaset doldu: deposit kasetine (kapasitesi tüm nominaller için ortak 100 not).
            if (remaining > 0)
            {
                var take = Math.Min(remaining, depositCassette.Capacity - depositCassette.Count);
                if (take > 0)
                {
                    var holding = depositCassette.Holdings.SingleOrDefault(h => h.Currency == currency && h.Denomination == denomination);
                    if (holding is null)
                    {
                        holding = new DepositHolding
                        {
                            Id = Guid.NewGuid(), CassetteId = depositCassette.Id, Currency = currency, Denomination = denomination,
                        };
                        db.DepositHoldings.Add(holding);
                    }

                    holding.Count += take;
                    depositCassette.Count += take;
                    remaining -= take;
                    db.CassetteMovements.Add(Movement(atm.Id, depositCassette.Id, take, currency, denomination, now, reference, user));
                    accepted.Add(new DepositPlacementDto(currency, denomination, take, depositCassette.Slot, CassetteType.Deposit,
                        matching.Count > 0 ? "OVERFLOW" : "NO_MATCHING_CASSETTE"));
                }
            }

            // 3) Deposit kaseti de doluysa sığmayanlar iade edilir.
            if (remaining > 0)
                returned.Add(new ReturnedNoteDto(currency, denomination, remaining, "DEPOSIT_CASSETTE_FULL"));
        }

        // Tanınmayan notlar işleme hiç girmez, doğrudan iade edilir.
        if (req.UnrecognizedCount > 0)
            returned.Add(new ReturnedNoteDto(null, null, req.UnrecognizedCount, "UNRECOGNISED"));

        var totals = accepted.GroupBy(a => a.Currency)
            .Select(g => new CurrencyTotalDto(g.Key, g.Sum(a => (long)a.Count * a.Denomination)))
            .OrderBy(t => t.Currency).ToList();

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new DepositResponse(atm.Id, accepted, returned, totals);
    }

    private static CassetteMovement Movement(Guid atmId, Guid cassetteId, int count, Currency currency, int denomination,
        DateTime now, string reference, string? user) => new()
    {
        Id = Guid.NewGuid(), AtmId = atmId, CassetteId = cassetteId, Type = MovementType.Deposit,
        CountDelta = count, ReservedDelta = 0, Currency = currency, Denomination = denomination,
        CreatedAt = now, Reference = reference, CreatedBy = user,
    };

    private static DomainException Invalid(string message) => new("INVALID_DEPOSIT", message);
}
