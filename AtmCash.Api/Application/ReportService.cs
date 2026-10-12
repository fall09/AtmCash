using System.Data;
using System.Text.Json;
using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

public class ReportService(AppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Operasyon raporu: ATM ve kaset bazında not/rezerve/kullanılabilir sayıları, para birimi toplamları ve TRY cinsinden genel toplam.
    /// Not sayıları ve toplamlar FİZİKSEL içeriktir (deposit kasetindekiler dahil). Deposit kasetinden para verilmediği için
    /// onun kullanılabilir (dağıtılabilir) sayısı 0 gösterilir.
    /// </summary>
    public async Task<OperationsReport> GetOperationsReportAsync(Guid? atmId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var query = db.Atms.AsNoTracking().Include(a => a.Cassettes).ThenInclude(c => c.Holdings).AsQueryable();
        if (atmId is not null) query = query.Where(a => a.Id == atmId);
        var atms = await query.OrderBy(a => a.Name).ToListAsync(ct);
        if (atmId is not null && atms.Count == 0) throw AtmLocking.AtmNotFound(atmId.Value);

        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, ct);
        await tx.CommitAsync(ct);

        var reportAtms = new List<ReportAtmDto>();
        var grand = new Dictionary<Currency, (int Notes, long Amount)>();

        foreach (var atm in atms)
        {
            var cassettes = new List<ReportCassetteDto>();
            var totals = new Dictionary<Currency, (int Notes, long Amount)>();

            void Add(Currency currency, int denomination, int count)
            {
                var (n, a) = totals.GetValueOrDefault(currency);
                totals[currency] = (n + count, a + (long)count * denomination);
                var (gn, ga) = grand.GetValueOrDefault(currency);
                grand[currency] = (gn + count, ga + (long)count * denomination);
            }

            foreach (var c in atm.Cassettes.OrderBy(c => c.Slot))
            {
                if (c.Type == CassetteType.Recycling)
                {
                    var low = c.IsConfigured && LowCashAlerts.IsLow(c.AvailableCount, c.Capacity);
                    cassettes.Add(new ReportCassetteDto(c.Slot, c.Type, c.Currency, c.Denomination, c.Capacity,
                        c.Count, c.ReservedCount, c.AvailableCount, low, null));
                    if (c.IsConfigured) Add(c.Currency!.Value, c.Denomination!.Value, c.Count);
                }
                else
                {
                    var contents = c.Holdings.Where(h => h.Count > 0).OrderBy(h => h.Currency).ThenBy(h => h.Denomination)
                        .Select(h => new DenominationTotalDto(h.Currency, h.Denomination, h.Count)).ToList();
                    cassettes.Add(new ReportCassetteDto(c.Slot, c.Type, null, null, c.Capacity, c.Count, c.ReservedCount, 0, false, contents));
                    foreach (var h in c.Holdings.Where(h => h.Count > 0)) Add(h.Currency, h.Denomination, h.Count);
                }
            }

            var currencyTotals = ToTotals(totals, rates);
            reportAtms.Add(new ReportAtmDto(atm.Id, atm.Name, atm.Status, cassettes, currencyTotals, currencyTotals.Sum(t => t.AmountTry)));
        }

        var overall = ToTotals(grand, rates);
        return new OperationsReport(clock.GetUtcNow().UtcDateTime, reportAtms, overall, overall.Sum(t => t.AmountTry));
    }

    public async Task<List<AlertDto>> GetAlertsAsync(Guid? atmId, int take, CancellationToken ct)
    {
        var query = db.OutboxMessages.AsNoTracking().AsQueryable();
        if (atmId is not null) query = query.Where(m => m.AtmId == atmId);
        var rows = await query.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return rows.Select(m => new AlertDto(m.Id, m.Type, m.AtmId, m.CreatedAt, m.SentAt,
            JsonDocument.Parse(m.Payload).RootElement.Clone())).ToList();
    }

    private static List<ReportCurrencyTotalDto> ToTotals(
        Dictionary<Currency, (int Notes, long Amount)> totals, Dictionary<Currency, decimal> rates) =>
        totals.OrderBy(t => t.Key)
            .Select(t => new ReportCurrencyTotalDto(t.Key, t.Value.Notes, t.Value.Amount,
                t.Value.Amount * (rates.TryGetValue(t.Key, out var r) ? r : throw new InvalidOperationException($"No exchange rate for {t.Key}."))))
            .ToList();
}
