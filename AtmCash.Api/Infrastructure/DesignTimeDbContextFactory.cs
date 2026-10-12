using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AtmCash.Api.Infrastructure;

/// <summary>
/// Sadece `dotnet ef migrations add` için: uygulamayı (ve açılıştaki migration'ı) çalıştırmadan
/// DbContext üretir. Gerçek veritabanına bağlanmaz.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=atmcash;Username=atm;Password=atm_pass")
            .Options;
        return new AppDbContext(options);
    }
}
