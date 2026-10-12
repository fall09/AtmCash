using System.Text.Json;
using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;

namespace AtmCash.Api.Application;

/// <summary>
/// B10: Geri dönüşümlü kasetin KULLANILABİLİR not sayısı kapasitenin %10'unun altına DÜŞTÜĞÜNDE uyarı üretir.
/// Sadece eşik aşağı geçilirken (önce >= %10, sonra &lt; %10) yazılır; aksi halde kaset düşükken her işlemde tekrar alarm çalardı.
/// Olay, çağıran servisin transaction'ına eklenir (SaveChanges çağıran tarafından yapılır) -> iş ve olay birlikte commit olur ya da birlikte geri alınır.
/// </summary>
public static class LowCashAlerts
{
    public const int ThresholdPercent = 10;

    public static bool IsLow(int availableCount, int capacity) => availableCount * 100 < capacity * ThresholdPercent;

    /// <summary>İşlemden ÖNCE çağrılır: kasetlerin o anki kullanılabilir not sayıları.</summary>
    public static Dictionary<Guid, int> Snapshot(Atm atm) =>
        atm.Cassettes.Where(c => c.Type == CassetteType.Recycling).ToDictionary(c => c.Id, c => c.AvailableCount);

    /// <summary>İşlemden SONRA, SaveChanges'tan önce çağrılır.</summary>
    public static void Record(AppDbContext db, Atm atm, IReadOnlyDictionary<Guid, int> before, DateTime now)
    {
        foreach (var c in atm.Cassettes.Where(c => c.Type == CassetteType.Recycling && c.IsConfigured))
        {
            if (!before.TryGetValue(c.Id, out var was)) continue;
            if (IsLow(was, c.Capacity) || !IsLow(c.AvailableCount, c.Capacity)) continue;

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = OutboxTypes.LowCash,
                AtmId = atm.Id,
                CreatedAt = now,
                Payload = JsonSerializer.Serialize(new
                {
                    atmId = atm.Id,
                    atmName = atm.Name,
                    slot = c.Slot,
                    currency = c.Currency!.Value.ToString(),
                    denomination = c.Denomination,
                    availableCount = c.AvailableCount,
                    capacity = c.Capacity,
                    thresholdPercent = ThresholdPercent,
                }),
            });
        }
    }
}
