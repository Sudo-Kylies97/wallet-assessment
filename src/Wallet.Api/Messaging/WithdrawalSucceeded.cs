namespace Wallet.Api.Messaging;

public sealed record WithdrawalSucceeded(Guid EventId, string EventType, Guid WithdrawalId,
    Guid WalletId, long AmountMinor, string Currency, long BalanceAfterMinor, DateTimeOffset OccurredAtUtc)
{
    public const string Type = "wallet.withdrawal.succeeded.v1";
}
