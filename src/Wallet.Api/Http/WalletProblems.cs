using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Features;

namespace Wallet.Api.Http;

public static class WalletProblems
{
    public static ObjectResult FromFailure(HttpContext context, WithdrawalFailure failure)
    {
        var (status, code, detail) = failure switch
        {
            WithdrawalFailure.InvalidAmount => (400, "invalid_request", "Amount must be a positive integer in minor units, no greater than 9007199254740991."),
            WithdrawalFailure.InvalidIdempotencyKey => (400, "invalid_idempotency_key", "Idempotency-Key must be a non-empty UUID in hyphenated format."),
            WithdrawalFailure.WalletNotFound => (404, "wallet_not_found", "Wallet does not exist."),
            WithdrawalFailure.InsufficientFunds => (409, "insufficient_funds", "The wallet has insufficient funds."),
            WithdrawalFailure.IdempotencyConflict => (409, "idempotency_conflict", "This key was already used with a different amount."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown withdrawal failure.")
        };
        return new ObjectResult(new ProblemDetails
        {
            Status = status, Title = code, Detail = detail, Instance = context.Request.Path,
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        }) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
