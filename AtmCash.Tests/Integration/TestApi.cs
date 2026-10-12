using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AtmCash.Tests.Integration;

public static class TestApi
{
    public static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendJsonAsync(
        this HttpClient client, HttpMethod method, string url, object? body = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (method != HttpMethod.Get) request.Content = JsonContent.Create(body ?? new { });
        foreach (var (name, value) in headers) request.Headers.Add(name, value);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>Yapılandırılmış, nakit yüklenmiş ve IN_SERVICE bir ATM kurar. Slotlar: 2=TRY100, 3=TRY50, 4=TRY20, 5=USD100.</summary>
    public static async Task<Guid> CreateInServiceAtmAsync(
        HttpClient c, int hundreds = 0, int fifties = 0, int twenties = 0, int usd = 0)
    {
        var (created, atm) = await c.SendJsonAsync(HttpMethod.Post, "/api/atms", new { name = "T-" + Guid.NewGuid().ToString("N")[..8] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse(atm!["id"]!.GetValue<string>());

        var (cfg, _) = await c.SendJsonAsync(HttpMethod.Put, $"/api/atms/{id}/cassettes", new
        {
            cassettes = new object[]
            {
                new { slot = 2, currency = "TRY", denomination = 100 },
                new { slot = 3, currency = "TRY", denomination = 50 },
                new { slot = 4, currency = "TRY", denomination = 20 },
                new { slot = 5, currency = "USD", denomination = 100 },
            }
        });
        Assert.Equal(HttpStatusCode.OK, cfg.StatusCode);

        var load = new List<object>();
        if (hundreds > 0) load.Add(new { slot = 2, count = hundreds });
        if (fifties > 0) load.Add(new { slot = 3, count = fifties });
        if (twenties > 0) load.Add(new { slot = 4, count = twenties });
        if (usd > 0) load.Add(new { slot = 5, count = usd });
        var (loaded, _) = await c.SendJsonAsync(HttpMethod.Post, $"/api/atms/{id}/load", new { cassettes = load });
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);

        var (svc, _) = await c.SendJsonAsync(HttpMethod.Post, $"/api/atms/{id}/transitions", new { target = "IN_SERVICE" });
        Assert.Equal(HttpStatusCode.OK, svc.StatusCode);
        return id;
    }

    public static async Task<JsonNode> CassetteAsync(HttpClient c, Guid atmId, int slot)
    {
        var (_, inv) = await c.SendJsonAsync(HttpMethod.Get, $"/api/atms/{atmId}");
        return inv!["cassettes"]!.AsArray().Single(x => x!["slot"]!.GetValue<int>() == slot)!;
    }
}
