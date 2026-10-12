using AtmCash.Api.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AtmCash.Api.Infrastructure;

/// <summary>
/// Yakalanmamış tüm hataları RFC 7807 ProblemDetails'e çevirir.
/// DomainException -> kendi status kodu + "code" uzantısı; diğerleri -> 500 (detay sızdırılmaz).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;

        if (exception is DomainException domain)
        {
            problem = new ProblemDetails
            {
                Status = domain.StatusCode,
                Title = domain.Message,
                Type = $"https://atmcash.local/errors/{domain.Code}"
            };
            problem.Extensions["code"] = domain.Code;
        }
        else
        {
            logger.LogError(exception, "Unhandled exception");
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred."
            };
        }

        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        // Doğrudan yazıyoruz: istemci Accept: text/plain gönderse bile (Swagger yapar) hata gövdesi kaybolmasın.
        httpContext.Response.StatusCode = problem.Status!.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json", ct);
        return true;
    }
}
