using System.Net;

namespace AtmCash.Tests.Integration;

[Collection("api")]
public class ReportingAndAlertTests(ApiFixture api)
{
    [Fact]
    public async Task LowCashAlert_IsWrittenOnceOnCrossing_AndPublishedByBackgroundService()
    {
        // Slot 2: 12 adet 100 TL. Kapasite 100 -> eşik: kullanılabilir < 10.
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 12);

        // 3 not rezerve edilince kullanılabilir 9'a düşer -> eşik aşağı geçildi -> uyarı.
        var (q1, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4777777777777771", currency = "TRY", amount = 300 });
        Assert.Equal(HttpStatusCode.Created, q1.StatusCode);

        // Zaten eşiğin altındayken yeni quote tekrar alarm üretmemeli.
        var (q2, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4777777777777772", currency = "TRY", amount = 100 });
        Assert.Equal(HttpStatusCode.Created, q2.StatusCode);

        var (_, alerts) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/alerts?atmId={atmId}");
        var alert = Assert.Single(alerts!.AsArray())!;
        Assert.Equal("LOW_CASH", alert["type"]!.GetValue<string>());
        Assert.Equal(2, alert["payload"]!["slot"]!.GetValue<int>());
        Assert.Equal(9, alert["payload"]!["availableCount"]!.GetValue<int>());
        Assert.Equal("TRY", alert["payload"]!["currency"]!.GetValue<string>());

        // Arka plan servisi olayı loglayıp "gönderildi" işaretlemeli (poll aralığı testte 1 sn).
        System.Text.Json.Nodes.JsonNode? sentAt = null;
        for (var i = 0; i < 40 && sentAt is null; i++)
        {
            await Task.Delay(500);
            var (_, latest) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/alerts?atmId={atmId}");
            sentAt = latest!.AsArray().Single()!["sentAt"];
        }

        Assert.NotNull(sentAt);
    }

    [Fact]
    public async Task NoAlert_WhenStockStaysAboveThreshold()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 50);

        await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4777777777777773", currency = "TRY", amount = 500 });

        var (_, alerts) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/alerts?atmId={atmId}");
        Assert.Empty(alerts!.AsArray());
    }

    [Fact]
    public async Task OperationsReport_ShowsCountsReservedAvailable_AndTotalsInTry()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 12, fifties: 5, usd: 3);
        await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "EUR", denomination = 50, count = 2 } } });
        // 2 adet 100 TL rezerve et
        var (q, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4777777777777774", currency = "TRY", amount = 200 });
        Assert.Equal(HttpStatusCode.Created, q.StatusCode);

        var (res, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/reports/operations?atmId={atmId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var atm = Assert.Single(report!["atms"]!.AsArray())!;
        var cassettes = atm["cassettes"]!.AsArray();

        var hundreds = cassettes.Single(c => c!["slot"]!.GetValue<int>() == 2)!;
        Assert.Equal(12, hundreds["noteCount"]!.GetValue<int>());
        Assert.Equal(2, hundreds["reservedCount"]!.GetValue<int>());
        Assert.Equal(10, hundreds["availableCount"]!.GetValue<int>());
        Assert.False(hundreds["lowCash"]!.GetValue<bool>());

        var usd = cassettes.Single(c => c!["slot"]!.GetValue<int>() == 5)!;
        Assert.True(usd["lowCash"]!.GetValue<bool>()); // 3 not < %10

        // Fiziksel toplamlar: TRY 12x100 + 5x50 = 1450, USD 3x100 = 300, EUR 2x50 = 100 (deposit kasetinde)
        var totals = atm["totalsByCurrency"]!.AsArray();
        decimal Amount(string currency) => totals.Single(t => t!["currency"]!.GetValue<string>() == currency)!["amount"]!.GetValue<decimal>();
        Assert.Equal(1450m, Amount("TRY"));
        Assert.Equal(300m, Amount("USD"));
        Assert.Equal(100m, Amount("EUR"));

        // Seed kurlarla: 1450 + 300 x 42 + 100 x 49 = 18.950 TRY
        Assert.Equal(18950m, atm["totalTry"]!.GetValue<decimal>());
    }
}
