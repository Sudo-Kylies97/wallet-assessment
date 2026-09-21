namespace Wallet.Api.Features;

public enum WithdrawalFailure
{
    InvalidAmount,
    InvalidIdempotencyKey,
    WalletNotFound,
    InsufficientFunds,
    IdempotencyConflict
}

public sealed record WithdrawalReceipt(Guid WithdrawalId, Guid WalletId, long AmountMinor,
    long BalanceAfterMinor, string Currency, DateTimeOffset OccurredAtUtc);

// Expected business outcomes carry no HTTP status, ProblemDetails, or transport DTO.
public abstract record WithdrawalResult
{
    private WithdrawalResult() { }
    public sealed record Accepted(WithdrawalReceipt Receipt) : WithdrawalResult;
    public sealed record Rejected(WithdrawalFailure Reason) : WithdrawalResult;
}
