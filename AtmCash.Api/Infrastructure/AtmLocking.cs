using AtmCash.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Infrastructure;

public static class AtmLocking
{
    /// <summary>
    /// ATM satırını SELECT ... FOR UPDATE ile kilitler (çağıran bir transaction içinde olmalı) ve
    /// kasetleriyle birlikte yükler. Aynı ATM üzerindeki işlemler sıraya girer; böylece aynı not
    /// iki kez rezerve edilemez (B6). Kilit sonrası okuma, commit edilmiş en güncel veriyi görür.
    /// </summary>
    public static async Task<Atm> LoadAtmForUpdateAsync(this AppDbContext db, Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Atms\" WHERE \"Id\" = {id} FOR UPDATE", ct);
        return await db.Atms.Include(a => a.Cassettes).SingleOrDefaultAsync(a => a.Id == id, ct)
               ?? throw AtmNotFound(id);
    }

    public static DomainException AtmNotFound(Guid id) =>
        new("ATM_NOT_FOUND", $"ATM {id} was not found.", StatusCodes.Status404NotFound);
}
