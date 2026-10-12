namespace AtmCash.Api.Application;

/// <summary>Her 5 saniyede süresi dolan quote'ların rezervasyonlarını serbest bırakır (B4).</summary>
public sealed class QuoteExpiryService(IServiceScopeFactory scopes, ILogger<QuoteExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<WithdrawalService>();
                    var n = await svc.ExpireDueQuotesAsync(stoppingToken);
                    if (n > 0) logger.LogInformation("Expired {Count} withdrawal quote(s); reservations released.", n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Quote expiry sweep failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // uygulama kapanıyor
        }
    }
}
