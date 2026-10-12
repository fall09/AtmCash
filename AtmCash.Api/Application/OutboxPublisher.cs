using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

/// <summary>Gönderilmemiş outbox olaylarını loglar ve "gönderildi" işaretler.</summary>
public class OutboxPublisher(AppDbContext db, TimeProvider clock, ILogger<OutboxPublisher> logger)
{
    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED: API birden fazla örnekle çalışsa bile aynı olayı iki örnek birden işlemez.
        var batch = await db.OutboxMessages.FromSqlInterpolated(
            $"""SELECT * FROM "OutboxMessages" WHERE "SentAt" IS NULL ORDER BY "CreatedAt" LIMIT 50 FOR UPDATE SKIP LOCKED""")
            .ToListAsync(ct);
        if (batch.Count == 0) return 0;

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var message in batch)
        {
            logger.LogWarning("OUTBOX EVENT {Type} (atm {AtmId}): {Payload}", message.Type, message.AtmId, message.Payload);
            message.SentAt = now;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}

public sealed class OutboxPublisherService(IServiceScopeFactory scopes, IConfiguration config, ILogger<OutboxPublisherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Outbox:PollSeconds", 5)));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().PublishPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Outbox publishing failed; will retry.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // uygulama kapanıyor
        }
    }
}
