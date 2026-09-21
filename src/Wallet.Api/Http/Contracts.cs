using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Wallet.Api.Features;

namespace Wallet.Api.Http;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WithdrawalRequest
{
    /// <summary>Positive integer ZAR cents. For example, 2500 represents ZAR 25.00.</summary>
    /// <example>2500</example>
    [Required, Range(1, Money.MaxSafeInteger)]
    public long AmountMinor { get; init; }
}

public sealed record BalanceResponse(Guid WalletId, string Currency, long BalanceMinor);
public sealed record WithdrawalResponse(Guid WithdrawalId, Guid WalletId, long AmountMinor,
    long BalanceAfterMinor, string Currency, DateTimeOffset OccurredAtUtc);
