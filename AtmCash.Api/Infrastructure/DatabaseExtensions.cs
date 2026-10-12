using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Infrastructure;

public static class DatabaseExtensions
{
    /// <summary>
    /// Açılışta bekleyen migration'ları uygular. docker compose ile veritabanı biraz geç
    /// hazır olabileceği için birkaç kez yeniden dener.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, int maxAttempts = 10)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("Database migrations applied.");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}/{Max}), retrying in 2s...", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
