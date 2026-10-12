namespace AtmCash.Api.Domain;

/// <summary>
/// İş kuralı ihlali. GlobalExceptionHandler bunu anlamlı bir ProblemDetails yanıtına çevirir.
/// Code alanı istemcinin (ve testlerin) hatayı makine okunur şekilde ayırt etmesini sağlar.
/// </summary>
public sealed class DomainException(string code, string message, int statusCode = StatusCodes.Status422UnprocessableEntity)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
