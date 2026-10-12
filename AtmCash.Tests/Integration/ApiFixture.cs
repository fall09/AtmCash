using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace AtmCash.Tests.Integration;

/// <summary>
/// Gerçek bir PostgreSQL konteyneri (Testcontainers) ayağa kaldırır ve API'yi bu veritabanına bağlı,
/// bellek içi test sunucusunda çalıştırır. Migration'lar uygulama açılışında otomatik uygulanır.
/// Docker'ın çalışıyor olması gerekir.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;
    public HttpClient Client { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        // Ortam değişkeni, Program.cs'in okuduğu yapılandırmayı güvenilir şekilde geçersiz kılar.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _pg.GetConnectionString());
        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _pg.DisposeAsync();
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;
