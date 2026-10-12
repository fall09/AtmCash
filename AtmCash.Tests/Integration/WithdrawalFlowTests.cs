using System.Net;

namespace AtmCash.Tests.Integration;

[Collection("api")]
public class WithdrawalFlowTests(ApiFixture api)
{
    [Fact]
    public async Task InvalidStateTransition_IsRejected_WithMeaningfulCode()
    {
        var (created, atm) = await api.Client.SendJsonAsync(HttpMethod.Post, "/api/atms", new { name = "Fresh-" + Guid.NewGuid().ToString("N")[..6] });
        var id = atm!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var (res, body) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{id}/transitions", new { target = "IN_SERVICE" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("INVALID_STATE_TRANSITION", body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Withdrawal_OnAtmThatIsNotInService_IsRejected()
    {
        var (_, atm) = await api.Client.SendJsonAsync(HttpMethod.Post, "/api/atms", new { name = "Idle-" + Guid.NewGuid().ToString("N")[..6] });
        var id = atm!["id"]!.GetValue<string>();

        var (res, body) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{id}/withdrawals/quotes",
            new { cardNumber = "4111111111111111", currency = "TRY", amount = 100 });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("INVALID_ATM_STATE", body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task QuoteConfirm_IsIdempotent_AndDebitsOnce()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 10);

        var (qRes, quote) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4333333333333333", currency = "TRY", amount = 200 });
        Assert.Equal(HttpStatusCode.Created, qRes.StatusCode);
        Assert.True(quote!["exactMatch"]!.GetValue<bool>());
        var quoteId = quote["quoteId"]!.GetValue<string>();

        var (first, firstBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm", new { }, ("Idempotency-Key", "k-1"));
        var (second, secondBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm", new { }, ("Idempotency-Key", "k-1"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody!.ToJsonString(), secondBody!.ToJsonString());
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var cassette = await TestApi.CassetteAsync(api.Client, atmId, 2);
        Assert.Equal(8, cassette["count"]!.GetValue<int>());
        Assert.Equal(0, cassette["reservedCount"]!.GetValue<int>());

        // Aynı quote'u FARKLI key ile onaylamak reddedilir.
        var (third, thirdBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quoteId}/confirm", new { }, ("Idempotency-Key", "k-2"));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("QUOTE_ALREADY_CONFIRMED", thirdBody!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Confirm_WithoutIdempotencyKey_IsRejected()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 5);
        var (_, quote) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4444444444444444", currency = "TRY", amount = 100 });

        var (res, body) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quote!["quoteId"]!.GetValue<string>()}/confirm", new { });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task DailyLimit_CountsPendingQuotes_PerCurrency()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 20, usd: 20);
        const string card = "4555555555555555";

        // USD günlük limit 1000: ilk quote 1000 USD'yi bekletir (henüz onaylanmadı).
        var (r1, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = card, currency = "USD", amount = 1000 });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        // Bekleyen tutar da limite sayılır: ikinci USD quote reddedilir.
        var (r2, b2) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = card, currency = "USD", amount = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r2.StatusCode);
        Assert.Equal("DAILY_LIMIT_EXCEEDED", b2!["code"]!.GetValue<string>());

        // Limitler para birimi bazında ayrı: aynı kart TRY çekebilir.
        var (r3, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = card, currency = "TRY", amount = 500 });
        Assert.Equal(HttpStatusCode.Created, r3.StatusCode);
    }
}
