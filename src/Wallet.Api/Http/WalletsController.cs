using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;
using Wallet.Api.Features;

namespace Wallet.Api.Http;

[ApiController]
[Route("api/wallets/{walletId}")]
public sealed class WalletsController(WalletDbContext db, WithdrawalService withdrawals) : ControllerBase
{
    /// <summary>Get the wallet's current balance in cents.</summary>
    /// <remarks>Seed wallet: 11111111-1111-1111-1111-111111111111. Initial balance: 100000 cents (ZAR 1,000.00).</remarks>
    /// <param name="walletId" example="11111111-1111-1111-1111-111111111111">Wallet UUID.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("balance")]
    [ProducesResponseType<BalanceResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
    public async Task<ActionResult<BalanceResponse>> GetBalance(Guid walletId, CancellationToken cancellationToken)
    {
        var result = await db.Wallets.AsNoTracking().Where(x => x.Id == walletId)
            .Select(x => new BalanceResponse(x.Id, x.Currency, x.BalanceMinor))
            .SingleOrDefaultAsync(cancellationToken);
        return result is null
            ? throw new WalletException(404, "wallet_not_found", "Wallet does not exist.")
            : Ok(result);
    }

    /// <summary>Withdraw funds exactly once per wallet and idempotency key.</summary>
    /// <remarks>
    /// Enter the seed wallet ID and a new UUID in Idempotency-Key; amountMinor 2500 withdraws ZAR 25.00.
    /// Retry with the SAME key and amount after a network error or 503. A repeated success returns the
    /// original receipt (its balance is historical; GET balance for the current value).
    /// Use a NEW key for a new withdrawal. Events are published asynchronously after commit.
    /// </remarks>
    /// <param name="walletId" example="11111111-1111-1111-1111-111111111111">Wallet UUID.</param>
    /// <param name="request">Positive integer amount in cents.</param>
    /// <param name="idempotencyKey" example="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa">Non-empty UUID in hyphenated format. Use a new key for a new withdrawal; reuse it for retries.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <response code="400">invalid_request for validation failures, or invalid_idempotency_key for a malformed or empty UUID header.</response>
    /// <response code="404">wallet_not_found: the wallet does not exist.</response>
    /// <response code="409">insufficient_funds if the balance cannot cover the amount; idempotency_conflict if the key previously succeeded with a different amount.</response>
    /// <response code="500">internal_error: retry an uncertain outcome with the same key and amount.</response>
    /// <response code="503">database_unavailable: retry with the same key and amount.</response>
    [HttpPost("withdrawals")]
    [ProducesResponseType<WithdrawalResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(404, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
    public async Task<ActionResult<WithdrawalResponse>> Withdraw(Guid walletId,
        [FromBody, Required] WithdrawalRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(idempotencyKey, "D", out var key) || key == Guid.Empty)
            throw new WalletException(400, "invalid_idempotency_key", "Idempotency-Key must be a non-empty UUID in hyphenated format.");
        return Ok(await withdrawals.WithdrawAsync(walletId, request.AmountMinor, key, cancellationToken));
    }
}
