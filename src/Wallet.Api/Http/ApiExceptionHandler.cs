using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Wallet.Api.Http;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            _ when IsUnavailable(exception) => (503, "database_unavailable", "Database unavailable. Retry with the same idempotency key and amount."),
            _ => (500, "internal_error", "An unexpected error occurred. Retry with the same idempotency key and amount.")
        };
        if (status >= 500) logger.LogError(exception, "Request failed: {Code}", code);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = code, Detail = detail, Instance = context.Request.Path,
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        }, options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } or TimeoutException ||
        exception.InnerException is { } inner && IsUnavailable(inner);
}
