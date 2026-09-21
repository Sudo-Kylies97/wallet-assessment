using System.Text.Json;
using Wallet.Api.Data;
using Wallet.Api.Messaging;

namespace Wallet.Api.Features;

public sealed class WithdrawalService(IWithdrawalStore store, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WithdrawalResult> WithdrawAsync(Guid walletId, long amountMinor,
        Guid idempotencyKey, CancellationToken cancellationToken)
    {
        if (amountMinor is <= 0 or > Money.MaxSafeInteger)
            return new WithdrawalResult.Rejected(WithdrawalFailure.InvalidAmount);
        if (idempotencyKey == Guid.Empty)
            return new WithdrawalResult.Rejected(WithdrawalFailure.InvalidIdempotencyKey);

        await using var transaction = await store.BeginAsync(cancellationToken);
        // The lock is held until commit/rollback, including the idempotency check.
        var wallet = await transaction.LockWalletAsync(walletId, cancellationToken);
        if (wallet is null)
            return new WithdrawalResult.Rejected(WithdrawalFailure.WalletNotFound);

        var existing = await transaction.FindWithdrawalAsync(walletId, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.AmountMinor != amountMinor)
                return new WithdrawalResult.Rejected(WithdrawalFailure.IdempotencyConflict);
            return Accepted(existing);
        }

        if (wallet.BalanceMinor < amountMinor)
            return new WithdrawalResult.Rejected(WithdrawalFailure.InsufficientFunds);

        var now = clock.GetUtcNow();
        // PostgreSQL timestamps retain microseconds; keep the first response identical to replays.
        now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        wallet.BalanceMinor -= amountMinor;
        var withdrawal = new Withdrawal
        {
            Id = Guid.NewGuid(), WalletId = walletId, IdempotencyKey = idempotencyKey,
            AmountMinor = amountMinor, BalanceAfterMinor = wallet.BalanceMinor,
            Currency = wallet.Currency, OccurredAtUtc = now
        };
        var eventId = Guid.NewGuid();
        var message = new WithdrawalSucceeded(eventId, WithdrawalSucceeded.Type, withdrawal.Id,
            walletId, amountMinor, wallet.Currency, wallet.BalanceMinor, now);
        var outbox = new OutboxMessage
        {
            Id = eventId, WithdrawalId = withdrawal.Id, EventType = WithdrawalSucceeded.Type,
            Payload = JsonSerializer.Serialize(message, Json), CreatedAtUtc = now, NextAttemptAtUtc = now
        };
        await transaction.SaveAsync(withdrawal, outbox, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Accepted(withdrawal);
    }

    private static WithdrawalResult.Accepted Accepted(Withdrawal withdrawal) => new(new WithdrawalReceipt(withdrawal.Id,
        withdrawal.WalletId, withdrawal.AmountMinor, withdrawal.BalanceAfterMinor,
        withdrawal.Currency, withdrawal.OccurredAtUtc));
}
