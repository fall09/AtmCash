using System.Net;

namespace AtmCash.Tests.Integration;

[Collection("api")]
public class ReconciliationTests(ApiFixture api)
{
    private static object Counts(int slot2, int slot3, int slot4, int slot5) => new
    {
        counts = new[]
        {
            new { slot = 2, countedCount = slot2 }, new { slot = 3, countedCount = slot3 },
            new { slot = 4, countedCount = slot4 }, new { slot = 5, countedCount = slot5 },
        }
    };

    /// <summary>Sistemde: slot2 TRY100 x50, slot3 TRY50 x10, slot4 TRY20 x10, slot5 USD100 x5; ATM MAINTENANCE'ta.</summary>
    private async Task<Guid> AtmInMaintenanceAsync()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 50, fifties: 10, twenties: 10, usd: 5);
        await ToMaintenanceAsync(atmId);
        return atmId;
    }

    private async Task ToMaintenanceAsync(Guid atmId)
    {
        var (res, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/transitions", new { target = "MAINTENANCE" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private Task<(HttpResponseMessage Response, System.Text.Json.Nodes.JsonNode? Body)> TryServiceAsync(Guid atmId) =>
        api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/transitions", new { target = "IN_SERVICE" });

    [Fact]
    public async Task SmallDifference_IsAppliedImmediately_AndNeedsNoApproval()
    {
        var atmId = await AtmInMaintenanceAsync();

        // 1 adet 100 TL eksik: toplam fark 100 TRY (eşik 1.000'in altında)
        var (res, recon) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            Counts(49, 10, 10, 5), ("X-User", "alice"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("COMPLETED", recon!["status"]!.GetValue<string>());
        Assert.Equal(100m, recon["totalAbsoluteDifferenceTry"]!.GetValue<decimal>());

        var cassette = await TestApi.CassetteAsync(api.Client, atmId, 2);
        Assert.Equal(49, cassette["count"]!.GetValue<int>());

        var (svc, _) = await TryServiceAsync(atmId);
        Assert.Equal(HttpStatusCode.OK, svc.StatusCode);

        var (_, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");
        Assert.True(report!["consistent"]!.GetValue<bool>(), report.ToJsonString());
    }

    [Fact]
    public async Task LargeDifference_BlocksService_UntilADifferentUserApproves()
    {
        var atmId = await AtmInMaintenanceAsync();

        // 11 adet 100 TL eksik = 1.100 TRY > 1.000
        var (res, recon) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            Counts(39, 10, 10, 5), ("X-User", "alice"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("AWAITING_APPROVAL", recon!["status"]!.GetValue<string>());
        var id = recon["id"]!.GetValue<string>();

        // Onay gelmeden servise dönülemez.
        var (blocked, blockedBody) = await TryServiceAsync(atmId);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("RECONCILIATION_PENDING_APPROVAL", blockedBody!["code"]!.GetValue<string>());

        // Başlatan kullanıcı onaylayamaz (büyük/küçük harf fark etmez).
        foreach (var same in new[] { "alice", "ALICE" })
        {
            var (denied, deniedBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/reconciliations/{id}/approve", new { }, ("X-User", same));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("SAME_USER_APPROVAL", deniedBody!["code"]!.GetValue<string>());
        }

        // X-User olmadan onaylanamaz.
        var (noUser, _) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/reconciliations/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);

        // Farklı kullanıcı onaylar.
        var (ok, approved) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/reconciliations/{id}/approve", new { }, ("X-User", "bob"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("APPROVED", approved!["status"]!.GetValue<string>());
        Assert.Equal("bob", approved["approvedBy"]!.GetValue<string>());

        // İkinci kez onaylamak anlamsız.
        var (again, againBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/reconciliations/{id}/approve", new { }, ("X-User", "carol"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("RECONCILIATION_NOT_AWAITING_APPROVAL", againBody!["code"]!.GetValue<string>());

        var (svc, _) = await TryServiceAsync(atmId);
        Assert.Equal(HttpStatusCode.OK, svc.StatusCode);

        // Düzeltme defterde: bakiye hareketlerle hâlâ tutarlı.
        var (_, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");
        Assert.True(report!["consistent"]!.GetValue<bool>(), report.ToJsonString());
        var (_, movements) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/movements?slot=2");
        Assert.Contains(movements!.AsArray(), m => m!["type"]!.GetValue<string>() == "RECONCILIATION_ADJUSTMENT"
                                                   && m["countDelta"]!.GetValue<int>() == -11);
    }

    [Fact]
    public async Task ForeignCurrencyDifference_IsValuedWithExchangeRate()
    {
        var atmId = await AtmInMaintenanceAsync();

        // 1 adet 100 USD eksik. Seed kur: 1 USD = 42 TRY -> 4.200 TRY
        var (_, recon) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            Counts(50, 10, 10, 4), ("X-User", "alice"));

        Assert.Equal("AWAITING_APPROVAL", recon!["status"]!.GetValue<string>());
        Assert.Equal(4200m, recon["totalAbsoluteDifferenceTry"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ShortagesAndSurpluses_DoNotCancelEachOther()
    {
        var atmId = await AtmInMaintenanceAsync();

        // slot2: -6 x 100 = -600, slot3: +12 x 50 = +600. Net 0 ama mutlak toplam 1.200 > 1.000.
        var (_, recon) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            Counts(44, 22, 10, 5), ("X-User", "alice"));

        Assert.Equal("AWAITING_APPROVAL", recon!["status"]!.GetValue<string>());
        Assert.Equal(1200m, recon["totalAbsoluteDifferenceTry"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task DepositCassette_CanBeReconciledPerDenomination()
    {
        var atmId = await TestApi.CreateInServiceAtmAsync(api.Client, hundreds: 5);
        await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/deposits",
            new { notes = new[] { new { currency = "EUR", denomination = 50, count = 10 } } });
        await ToMaintenanceAsync(atmId);

        // Sistem: deposit kasetinde 10 x EUR 50. Sayım: 8 -> 2 x 50 x 49 = 4.900 TRY.
        var (res, recon) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations", new
        {
            counts = new[]
            {
                new { slot = 2, countedCount = 5 }, new { slot = 3, countedCount = 0 },
                new { slot = 4, countedCount = 0 }, new { slot = 5, countedCount = 0 },
            },
            depositCounts = new[] { new { currency = "EUR", denomination = 50, countedCount = 8 } },
        }, ("X-User", "alice"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(4900m, recon!["totalAbsoluteDifferenceTry"]!.GetValue<decimal>());

        var deposit = await TestApi.CassetteAsync(api.Client, atmId, 1);
        Assert.Equal(8, deposit["count"]!.GetValue<int>());

        var (_, report) = await api.Client.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}/ledger/consistency");
        Assert.True(report!["consistent"]!.GetValue<bool>(), report.ToJsonString());
    }

    [Fact]
    public async Task Reconcile_RequiresUser_Maintenance_AndAllCassettes()
    {
        var atmId = await AtmInMaintenanceAsync();

        var (noUser, noUserBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations", Counts(50, 10, 10, 5));
        Assert.Equal(HttpStatusCode.BadRequest, noUser.StatusCode);
        Assert.Equal("USER_REQUIRED", noUserBody!["code"]!.GetValue<string>());

        var (partial, partialBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            new { counts = new[] { new { slot = 2, countedCount = 50 } } }, ("X-User", "alice"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
        Assert.Equal("INVALID_RECONCILIATION", partialBody!["code"]!.GetValue<string>());

        await TryServiceAsync(atmId);
        var (wrongState, wrongBody) = await api.Client.SendJsonAsync(HttpMethod.Post, $"/api/atms/{atmId}/reconciliations",
            Counts(50, 10, 10, 5), ("X-User", "alice"));
        Assert.Equal(HttpStatusCode.Conflict, wrongState.StatusCode);
        Assert.Equal("INVALID_ATM_STATE", wrongBody!["code"]!.GetValue<string>());
    }
}
