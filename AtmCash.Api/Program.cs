using System.Text.Json;
using System.Text.Json.Serialization;
using AtmCash.Api.Application;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Enum'lar API'de SNAKE_UPPER string olarak görünür (IN_SERVICE, MAINTENANCE ...).
JsonStringEnumConverter EnumConverter() => new(JsonNamingPolicy.SnakeCaseUpper);
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(EnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(EnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CardHasher>();
builder.Services.AddScoped<AtmService>();
builder.Services.AddScoped<WithdrawalService>();
builder.Services.AddHostedService<QuoteExpiryService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

await app.Services.MigrateDatabaseAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger UI: dokümanı .NET'in yerleşik OpenAPI üretici verir, arayüzü Swashbuckle.SwaggerUI çizer.
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "AtmCash API v1"));

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Integration testlerde WebApplicationFactory<Program> için gerekli.
public partial class Program;
