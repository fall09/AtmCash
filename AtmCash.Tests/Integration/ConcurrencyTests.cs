using System.Net;

namespace AtmCash.Tests.Integration;

[Collection("api")]
public class ConcurrencyTests(ApiFixture api)
{
    /// <summary>
    /// B6: ATM'de 10 adet 100 TL notu var. Aynı anda 30 farklı kart 100 TL için quote ister.
    /// Tam 10 istek başarılı olmalı; hiçbir not iki kez rezerve edilmemeli.
    /// Kilit (SELECT ... FOR UPDATE) olmasaydı 10'dan fazla istek başarılı olur ya da CHECK kısıtı patlardı.
    /// </summary>
    [Fact]
    public async Task ParallelQuotes_NeverReserveTheSameNoteTwice()
    {
        const int stock = 10;
        const int parallel = 30;
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: stock);

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, parallel).Select(async i =>
        {
            await gate.Task; // hepsi aynı anda başlasın
            return await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
                new { cardNumber = $"41111111{i:D8}", currency = "TRY", amount = 100 });
        }).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        var created = results.Count(r => r.Response.StatusCode == HttpStatusCode.Created);
        var rejected = results.Where(r => r.Response.StatusCode == HttpStatusCode.UnprocessableEntity).ToList();

        Assert.Equal(stock, created);
        Assert.Equal(parallel - stock, rejected.Count);
        Assert.All(rejected, r => Assert.Equal("NO_PAYABLE_AMOUNT", r.Body!["code"]!.GetValue<string>()));

        var cassette = await TestApi.CassetteAsync(api.Client, atmId, 2);
        Assert.Equal(stock, cassette["count"]!.GetValue<int>());
        Assert.Equal(stock, cassette["reservedCount"]!.GetValue<int>());
        Assert.Equal(0, cassette["availableCount"]!.GetValue<int>());
    }

    /// <summary>Aynı idempotency key ile eşzamanlı gelen confirm istekleri nakdi yalnızca bir kez düşmeli.</summary>
    [Fact]
    public async Task ParallelConfirms_WithSameKey_DebitOnlyOnce()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 10);
        var (qRes, quote) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4222222222222222", currency = "TRY", amount = 300 });
        Assert.Equal(HttpStatusCode.Created, qRes.StatusCode);
        var quoteId = quote!["quoteId"]!.GetValue<string>();

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await gate.Task;
            return await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm", new { },
                ("Idempotency-Key", "same-key-1"));
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));
        var cassette = await TestApi.CassetteAsync(api.Client, atmId, 2);
        Assert.Equal(7, cassette["count"]!.GetValue<int>()); // 10 - 3, bir kez düştü
        Assert.Equal(0, cassette["reservedCount"]!.GetValue<int>());
    }
}
