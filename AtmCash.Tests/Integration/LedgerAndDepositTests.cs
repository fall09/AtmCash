using System.Net;
using AtmCash.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Tests.Integration;

[Collection("api")]
public class LedgerAndDepositTests(ApiFixture api)
{
    [Fact]
    public async Task Ledger_RecordsEveryChange_AndStaysConsistent()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 10);

        var (_, quote) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/withdrawals/quotes",
            new { cardNumber = "4666666666666666", currency = "TRY", amount = 200 });
        var (_, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/quotes/{quote!["quoteId"]!.GetValue<string>()}/confirm", new { },
            ("Idempotency-Key", "ledger-1"));
        var (dep, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "TRY", denomination = 100, count = 5 }, new { currency = "EUR", denomination = 20, count = 3 } } });
        Assert.Equal(HttpStatusCode.OK, dep.StatusCode);

        var (_, movements) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/movements");
        var types = movements!.AsArray().Select(m => m!["type"]!.GetValue<string>()).ToHashSet();
        Assert.Superset(new HashSet<string> { "LOAD", "RESERVATION", "WITHDRAWAL", "RESERVATION_RELEASE", "DEPOSIT" }, types);

        var (res, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(report!["consistent"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Empty(report["differences"]!.AsArray());
    }

    [Fact]
    public async Task ConsistencyCheck_DetectsTamperedBalance()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 10);

        // Birisi veritabanında bakiyeyi ledger'ı atlayarak değiştirirse yakalanmalı.
        using (var scope = api.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Cassettes" SET "Count" = "Count" + 3 WHERE "AtmId" = {atmId} AND "Slot" = 2""");
        }

        var (_, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");

        Assert.False(report!["consistent"]!.GetValue<bool>());
        var difference = Assert.Single(report["differences"]!.AsArray())!.GetValue<string>();
        Assert.Contains("Cassette 2", difference);
        Assert.Contains("difference 3", difference);
    }

    [Fact]
    public async Task Movements_AreImmutable_AtDatabaseLevel()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 1);

        using var scope = api.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "CassetteMovements" SET "CountDelta" = 999 WHERE "AtmId" = {atmId}"""));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "CassetteMovements" WHERE "AtmId" = {atmId}"""));
    }

    [Fact]
    public async Task Deposit_Overflows_ToDepositCassette_ThenReturnsWhatDoesNotFit()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 98);

        // 100'lük kasette 2 yer var: 5 notun 2'si kasete, 3'ü deposit kasetine taşar.
        var (r1, b1) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "TRY", denomination = 100, count = 5 } }, unrecognizedCount = 0 });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var accepted = b1!["accepted"]!.AsArray();
        Assert.Equal(2, accepted.Count);
        Assert.Equal(("MATCHING_CASSETTE", 2, 2), (accepted[0]!["reason"]!.GetValue<string>(), accepted[0]!["cassetteSlot"]!.GetValue<int>(), accepted[0]!["count"]!.GetValue<int>()));
        Assert.Equal(("OVERFLOW", 1, 3), (accepted[1]!["reason"]!.GetValue<string>(), accepted[1]!["cassetteSlot"]!.GetValue<int>(), accepted[1]!["count"]!.GetValue<int>()));
        Assert.Empty(b1["returned"]!.AsArray());

        // Deposit kasetinde 97 yer kaldı: 120 EUR 50'lik notun 97'si girer, 23'ü iade edilir.
        var (r2, b2) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "EUR", denomination = 50, count = 120 } }, unrecognizedCount = 4 });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var returned = b2!["returned"]!.AsArray();
        Assert.Equal(2, returned.Count);
        Assert.Equal(23, returned.Single(x => x!["reason"]!.GetValue<string>() == "DEPOSIT_CASSETTE_FULL")!["count"]!.GetValue<int>());
        Assert.Equal(4, returned.Single(x => x!["reason"]!.GetValue<string>() == "UNRECOGNISED")!["count"]!.GetValue<int>());

        var deposit = await TestApi.CassetteAsync(api.Client, atmId, 1);
        Assert.Equal(100, deposit["count"]!.GetValue<int>());

        // Ledger deposit kasetinin nominal bazlı içeriğini de doğrulayabilmeli.
        var (_, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");
        Assert.True(report!["consistent"]!.GetValue<bool>(), report.ToJsonString());
    }

    [Fact]
    public async Task Deposit_IsRejected_WhenAtmIsNotInService()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 5);
        await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/transitions", new { target = "MAINTENANCE" });

        var (res, body) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "TRY", denomination = 100, count = 1 } } });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("INVALID_ATM_STATE", body!["code"]!.GetValue<string>());
    }
}
