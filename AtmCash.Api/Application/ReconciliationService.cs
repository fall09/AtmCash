using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

/// <summary>
/// B9: Elle sayım ve mutabakat. Fark hemen RECONCILIATION_ADJUSTMENT hareketiyle uygulanır.
/// Farkların toplam (mutlak) TRY değeri eşiği aşarsa mutabakat AWAITING_APPROVAL olur ve ATM,
/// mutabakatı başlatandan FARKLI bir kullanıcı (X-User) onaylayana kadar servise dönemez.
/// </summary>
public class ReconciliationService(AppDbContext db, TimeProvider clock)
{
    public const decimal ApprovalThresholdTry = 1000m;

    public async Task<ReconciliationDto> ReconcileAsync(Guid atmId, ReconcileRequest req, string? user, CancellationToken ct)
    {
        RequireUser(user);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
        atm.EnsureState("reconcile", AtmStatus.Maintenance);

        var now = clock.GetUtcNow().UtcDateTime;
        var availableBefore = LowCashAlerts.Snapshot(atm);
        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, ct);
        decimal Rate(Currency c) => rates.TryGetValue(c, out var r)
            ? r
            : throw new InvalidOperationException($"No exchange rate configured for {c}.");

        var recon = new Reconciliation { Id = Guid.NewGuid(), AtmId = atm.Id, StartedBy = user!.Trim(), CreatedAt = now };

        // ---- Geri dönüşümlü kasetler: her biri için sayım zorunlu
        var recycling = atm.Cassettes.Where(c => c.Type == CassetteType.Recycling).OrderBy(c => c.Slot).ToList();
        var givenSlots = req.Counts.Select(c => c.Slot).OrderBy(s => s).ToList();
        if (!givenSlots.SequenceEqual(recycling.Select(c => c.Slot).ToList()))
            throw Invalid("A counted number must be entered for each recycling cassette (slots 2-5), exactly once.");

        foreach (var c in recycling)
        {
            var counted = req.Counts.Single(x => x.Slot == c.Slot).CountedCount;
            if (counted < 0 || counted > c.Capacity)
                throw Invalid($"Counted number for cassette {c.Slot} must be between 0 and {c.Capacity}.");
            if (counted < c.ReservedCount)
                throw Invalid($"Cassette {c.Slot} has {c.ReservedCount} reserved notes; counted number cannot be lower.");

            var diff = counted - c.Count;
            recon.Lines.Add(new ReconciliationLine
            {
                Id = Guid.NewGuid(), Slot = c.Slot, CassetteId = c.Id, Currency = c.Currency, Denomination = c.Denomination,
                SystemCount = c.Count, CountedCount = counted,
                ValueTry = diff * c.Denomination!.Value * Rate(c.Currency!.Value),
            });

            if (diff != 0)
            {
                c.Count = counted;
                db.CassetteMovements.Add(Adjustment(atm.Id, c.Id, diff, c.Currency, c.Denomination, now, recon.Id, user));
            }
        }

        // ---- Deposit kaseti (opsiyonel): nominal bazında tam sayım
        if (req.DepositCounts is not null)
        {
            var deposit = atm.Cassettes.Single(c => c.Type == CassetteType.Deposit);

            foreach (var n in req.DepositCounts)
            {
                if (!Denominations.Allowed.Contains(n.Denomination))
                    throw Invalid($"Denomination {n.Denomination} is not supported.");
                if (n.CountedCount < 0)
                    throw Invalid("Counted numbers cannot be negative.");
            }

            if (req.DepositCounts.GroupBy(n => (Currency: n.Currency!.Value, n.Denomination)).Any(g => g.Count() > 1))
                throw Invalid("Each currency/denomination may appear only once in depositCounts.");

            var provided = req.DepositCounts.ToDictionary(n => (Currency: n.Currency!.Value, Denomination: n.Denomination), n => n.CountedCount);
            if (provided.Values.Sum() > deposit.Capacity)
                throw Invalid($"The deposit cassette holds at most {deposit.Capacity} notes.");

            var keys = deposit.Holdings.Select(h => (Currency: h.Currency, Denomination: h.Denomination))
                .Union(provided.Keys)
                .OrderBy(k => k.Currency).ThenBy(k => k.Denomination)
                .ToList();

            foreach (var (currency, denomination) in keys)
            {
                var holding = deposit.Holdings.FirstOrDefault(h => h.Currency == currency && h.Denomination == denomination);
                var system = holding?.Count ?? 0;
                var counted = provided.GetValueOrDefault((currency, denomination));
                var diff = counted - system;

                recon.Lines.Add(new ReconciliationLine
                {
                    Id = Guid.NewGuid(), Slot = deposit.Slot, CassetteId = deposit.Id, Currency = currency, Denomination = denomination,
                    SystemCount = system, CountedCount = counted,
                    ValueTry = diff * denomination * Rate(currency),
                });

                if (diff == 0) continue;

                if (holding is null)
                {
                    holding = new DepositHolding { Id = Guid.NewGuid(), CassetteId = deposit.Id, Currency = currency, Denomination = denomination };
                    db.DepositHoldings.Add(holding);
                }

                holding.Count = counted;
                deposit.Count += diff;
                db.CassetteMovements.Add(Adjustment(atm.Id, deposit.Id, diff, currency, denomination, now, recon.Id, user));
            }
        }

        recon.TotalAbsoluteDifferenceTry = recon.Lines.Sum(l => Math.Abs(l.ValueTry));
        recon.Status = recon.TotalAbsoluteDifferenceTry > ApprovalThresholdTry
            ? ReconciliationStatus.AwaitingApproval
            : ReconciliationStatus.Completed;

        LowCashAlerts.Record(db, atm, availableBefore, now);

        db.Reconciliations.Add(recon);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(recon);
    }

    public async Task<ReconciliationDto> ApproveAsync(Guid reconciliationId, string? user, CancellationToken ct)
    {
        RequireUser(user);

        var atmId = await db.Reconciliations.AsNoTracking().Where(r => r.Id == reconciliationId)
                        .Select(r => (Guid?)r.AtmId).SingleOrDefaultAsync(ct)
                    ?? throw NotFound(reconciliationId);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LoadAtmForUpdateAsync(atmId, ct); // ATM kilidi: onay ile servise dönüş yarışmasın
        var recon = await db.Reconciliations.Include(r => r.Lines).SingleAsync(r => r.Id == reconciliationId, ct);

        if (recon.Status != ReconciliationStatus.AwaitingApproval)
            throw new DomainException("RECONCILIATION_NOT_AWAITING_APPROVAL",
                $"Reconciliation is {recon.Status} and does not need approval.", StatusCodes.Status409Conflict);

        var approver = user!.Trim();
        if (string.Equals(approver, recon.StartedBy, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("SAME_USER_APPROVAL",
                "Four-eyes rule: a reconciliation must be approved by a different user than the one who started it.",
                StatusCodes.Status403Forbidden);

        recon.Status = ReconciliationStatus.Approved;
        recon.ApprovedBy = approver;
        recon.ApprovedAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(recon);
    }

    public async Task<ReconciliationDto> GetAsync(Guid id, CancellationToken ct)
    {
        var recon = await db.Reconciliations.AsNoTracking().Include(r => r.Lines)
                        .SingleOrDefaultAsync(r => r.Id == id, ct)
                    ?? throw NotFound(id);
        return ToDto(recon);
    }

    public async Task<List<ReconciliationDto>> ListAsync(Guid atmId, CancellationToken ct)
    {
        if (!await db.Atms.AnyAsync(a => a.Id == atmId, ct)) throw AtmLocking.AtmNotFound(atmId);
        var list = await db.Reconciliations.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.AtmId == atmId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<List<ExchangeRateDto>> GetRatesAsync(CancellationToken ct) =>
        await db.ExchangeRates.AsNoTracking().OrderBy(r => r.Currency)
            .Select(r => new ExchangeRateDto(r.Currency, r.RateToTry)).ToListAsync(ct);

    // ---- yardımcılar
    private static void RequireUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new DomainException("USER_REQUIRED", "The X-User header is required.", StatusCodes.Status400BadRequest);
        if (user.Length > 100)
            throw new DomainException("USER_INVALID", "X-User must be at most 100 characters.", StatusCodes.Status400BadRequest);
    }

    private static CassetteMovement Adjustment(Guid atmId, Guid cassetteId, int diff, Currency? currency, int? denomination,
        DateTime now, Guid reconciliationId, string? user) => new()
    {
        Id = Guid.NewGuid(), AtmId = atmId, CassetteId = cassetteId, Type = MovementType.ReconciliationAdjustment,
        CountDelta = diff, ReservedDelta = 0, Currency = currency, Denomination = denomination,
        CreatedAt = now, Reference = $"reconciliation:{reconciliationId}", CreatedBy = user!.Trim(),
    };

    private static DomainException Invalid(string message) => new("INVALID_RECONCILIATION", message);

    private static DomainException NotFound(Guid id) =>
        new("RECONCILIATION_NOT_FOUND", $"Reconciliation {id} was not found.", StatusCodes.Status404NotFound);

    private static ReconciliationDto ToDto(Reconciliation r) => new(
        r.Id, r.AtmId, r.Status, r.StartedBy, r.CreatedAt, r.TotalAbsoluteDifferenceTry, ApprovalThresholdTry,
        r.ApprovedBy, r.ApprovedAt,
        r.Lines.OrderBy(l => l.Slot).ThenBy(l => l.Currency).ThenBy(l => l.Denomination)
            .Select(l => new ReconciliationLineDto(l.Slot, l.Currency, l.Denomination, l.SystemCount, l.CountedCount, l.Difference, l.ValueTry))
            .ToList());
}
