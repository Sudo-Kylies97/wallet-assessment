namespace Wallet.Api.Data;

public sealed class WalletAccount
{
    public Guid Id { get; set; }
    public string Currency { get; set; } = "ZAR";
    public long BalanceMinor { get; set; }
}

public sealed class Withdrawal
{
    public Guid Id { get; set; }
    public Guid WalletId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public long AmountMinor { get; set; }
    public long BalanceAfterMinor { get; set; }
    public string Currency { get; set; } = "ZAR";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid WithdrawalId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}
