using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Wallet.Api.Features;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WithdrawalRequest
{
    /// <summary>Positive integer ZAR cents. For example, 2500 represents ZAR 25.00.</summary>
    /// <example>2500</example>
    [Required, Range(1, Money.MaxSafeInteger)]
    public long AmountMinor { get; init; }
}

public static class Money
{
    public const long MaxSafeInteger = 9_007_199_254_740_991;
}

public sealed record BalanceResponse(Guid WalletId, string Currency, long BalanceMinor);
public sealed record WithdrawalResponse(Guid WithdrawalId, Guid WalletId, long AmountMinor,
    long BalanceAfterMinor, string Currency, DateTimeOffset OccurredAtUtc);

public sealed record WithdrawalSucceeded(Guid EventId, string EventType, Guid WithdrawalId,
    Guid WalletId, long AmountMinor, string Currency, long BalanceAfterMinor, DateTimeOffset OccurredAtUtc)
{
    public const string Type = "wallet.withdrawal.succeeded.v1";
}

public sealed class WalletException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
