using System.Text.Json;
using Wallet.Api.Data;
using Wallet.Api.Features;
using Wallet.Api.Messaging;

namespace Wallet.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class WithdrawalServiceTests
{
    private static readonly Guid WalletId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Key = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 21, 10, 30, 0, TimeSpan.Zero).AddTicks(1234567);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Money.MaxSafeInteger + 1)]
    [InlineData(long.MaxValue)]
    public async Task Invalid_amount_is_a_rejected_outcome_without_opening_storage(long amount)
    {
        var store = new MemoryStore(NewWallet());
        var result = await Service(store).WithdrawAsync(WalletId, amount, Key, CancellationToken.None);
        Assert.Equal(WithdrawalFailure.InvalidAmount, Assert.IsType<WithdrawalResult.Rejected>(result).Reason);
        Assert.False(store.Began);
    }

    [Fact]
    public async Task Empty_key_is_rejected_for_non_HTTP_callers_too()
    {
        var store = new MemoryStore(NewWallet());
        var result = await Service(store).WithdrawAsync(WalletId, 100, Guid.Empty, CancellationToken.None);
        Assert.Equal(WithdrawalFailure.InvalidIdempotencyKey, Assert.IsType<WithdrawalResult.Rejected>(result).Reason);
        Assert.False(store.Began);
    }

    [Fact]
    public async Task Unknown_wallet_returns_a_business_outcome_and_closes_the_transaction()
    {
        var store = new MemoryStore(null);
        var result = await Service(store).WithdrawAsync(WalletId, 100, Key, CancellationToken.None);
        Assert.Equal(WithdrawalFailure.WalletNotFound, Assert.IsType<WithdrawalResult.Rejected>(result).Reason);
        Assert.True(store.Disposed);
        Assert.False(store.Committed);
        Assert.Equal(0, store.WriteAttempts);
    }

    [Theory]
    [InlineData(100001)]
    [InlineData(Money.MaxSafeInteger)]
    public async Task Insufficient_funds_returns_without_debit_receipt_or_event(long amount)
    {
        var store = new MemoryStore(NewWallet());
        var result = await Service(store).WithdrawAsync(WalletId, amount, Key, CancellationToken.None);
        Assert.Equal(WithdrawalFailure.InsufficientFunds, Assert.IsType<WithdrawalResult.Rejected>(result).Reason);
        Assert.Equal(100000, store.Wallet!.BalanceMinor);
        Assert.Null(store.StoredReceipt);
        Assert.Null(store.StoredEvent);
        Assert.Equal(0, store.WriteAttempts);
        Assert.True(store.Disposed);
    }

    [Theory]
    [InlineData(2500, 97500)]
    [InlineData(100000, 0)]
    public async Task Success_uses_the_injected_clock_and_commits_a_consistent_receipt_and_event(long amount, long balance)
    {
        var store = new MemoryStore(NewWallet());
        var result = await Service(store).WithdrawAsync(WalletId, amount, Key, CancellationToken.None);
        var receipt = Assert.IsType<WithdrawalResult.Accepted>(result).Receipt;
        Assert.Equal(balance, store.Wallet!.BalanceMinor);
        Assert.Equal(balance, receipt.BalanceAfterMinor);
        Assert.Equal(amount, receipt.AmountMinor);
        Assert.Equal(WalletId, receipt.WalletId);
        Assert.Equal("ZAR", receipt.Currency);
        Assert.Equal(Now.AddTicks(-7), receipt.OccurredAtUtc);
        Assert.NotNull(store.StoredReceipt);
        Assert.Equal(Key, store.StoredReceipt.IdempotencyKey);
        Assert.Equal(receipt.WithdrawalId, store.StoredReceipt.Id);
        Assert.NotNull(store.StoredEvent);
        Assert.Equal(receipt.OccurredAtUtc, store.StoredEvent.CreatedAtUtc);
        Assert.Equal(receipt.OccurredAtUtc, store.StoredEvent.NextAttemptAtUtc);
        Assert.Null(store.StoredEvent.PublishedAtUtc);
        var payload = JsonSerializer.Deserialize<WithdrawalSucceeded>(store.StoredEvent.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(store.StoredEvent.Id, payload.EventId);
        Assert.Equal(receipt.WithdrawalId, payload.WithdrawalId);
        Assert.Equal(WalletId, payload.WalletId);
        Assert.Equal(amount, payload.AmountMinor);
        Assert.Equal(balance, payload.BalanceAfterMinor);
        Assert.Equal(receipt.OccurredAtUtc, payload.OccurredAtUtc);
        Assert.True(store.Committed);
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task Replay_preserves_the_original_receipt_even_when_current_funds_are_zero()
    {
        var previous = PriorWithdrawal();
        var store = new MemoryStore(NewWallet(0)) { Existing = previous };
        var result = await Service(store).WithdrawAsync(WalletId, 2500, Key, CancellationToken.None);
        var receipt = Assert.IsType<WithdrawalResult.Accepted>(result).Receipt;
        Assert.Equal(previous.Id, receipt.WithdrawalId);
        Assert.Equal(previous.BalanceAfterMinor, receipt.BalanceAfterMinor);
        Assert.Equal(previous.OccurredAtUtc, receipt.OccurredAtUtc);
        Assert.Equal(0, store.Wallet!.BalanceMinor);
        Assert.Equal(0, store.WriteAttempts);
        Assert.False(store.Committed);
        Assert.True(store.Disposed);
    }

    [Fact]
    public async Task Reusing_a_key_with_another_amount_returns_a_conflict_without_writing()
    {
        var store = new MemoryStore(NewWallet()) { Existing = PriorWithdrawal() };
        var result = await Service(store).WithdrawAsync(WalletId, 2600, Key, CancellationToken.None);
        Assert.Equal(WithdrawalFailure.IdempotencyConflict, Assert.IsType<WithdrawalResult.Rejected>(result).Reason);
        Assert.Equal(100000, store.Wallet!.BalanceMinor);
        Assert.Equal(0, store.WriteAttempts);
        Assert.True(store.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_storage_failures_propagate_and_dispose_without_a_successful_commit(bool failCommit)
    {
        var failure = new IOException("Storage failure");
        var store = new MemoryStore(NewWallet())
        {
            SaveError = failCommit ? null : failure,
            CommitError = failCommit ? failure : null
        };
        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            Service(store).WithdrawAsync(WalletId, 2500, Key, CancellationToken.None));
        Assert.Same(failure, thrown);
        Assert.True(store.Disposed);
        Assert.False(store.Committed);
        Assert.Equal(100000, store.Wallet!.BalanceMinor);
        Assert.Null(store.StoredReceipt);
        Assert.Null(store.StoredEvent);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_into_a_business_rejection()
    {
        var store = new MemoryStore(NewWallet());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(store).WithdrawAsync(WalletId, 2500, Key, cancellation.Token));
        Assert.False(store.Committed);
        Assert.Null(store.StoredEvent);
    }

    private static WithdrawalService Service(MemoryStore store) => new(store, new FixedClock(Now));
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static WalletAccount NewWallet(long balance = 100000) => new() { Id = WalletId, Currency = "ZAR", BalanceMinor = balance };
    private static Withdrawal PriorWithdrawal() => new()
    {
        Id = Guid.NewGuid(), WalletId = WalletId, IdempotencyKey = Key, AmountMinor = 2500,
        BalanceAfterMinor = 97500, Currency = "ZAR", OccurredAtUtc = Now.AddDays(-1).AddTicks(-7)
    };

    // Transactional test double for service decisions. Real locking and rollback are
    // deliberately verified separately against PostgreSQL in the integration suite.
    private sealed class MemoryStore(WalletAccount? wallet) : IWithdrawalStore
    {
        public WalletAccount? Wallet { get; } = wallet;
        public Withdrawal? Existing { get; init; }
        public Withdrawal? StoredReceipt { get; private set; }
        public OutboxMessage? StoredEvent { get; private set; }
        public Exception? SaveError { get; init; }
        public Exception? CommitError { get; init; }
        public bool Began { get; private set; }
        public bool Disposed { get; private set; }
        public bool Committed { get; private set; }
        public int WriteAttempts { get; private set; }

        public Task<IWithdrawalTransaction> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Began = true;
            return Task.FromResult<IWithdrawalTransaction>(new Transaction(this));
        }

        private sealed class Transaction(MemoryStore store) : IWithdrawalTransaction
        {
            private WalletAccount? workingWallet;
            private Withdrawal? receipt;
            private OutboxMessage? message;

            public Task<WalletAccount?> LockWalletAsync(Guid walletId, CancellationToken cancellationToken)
            {
                if (store.Wallet is { } current && current.Id == walletId)
                    workingWallet = new WalletAccount { Id = current.Id, Currency = current.Currency, BalanceMinor = current.BalanceMinor };
                return Task.FromResult(workingWallet);
            }

            public Task<Withdrawal?> FindWithdrawalAsync(Guid walletId, Guid key, CancellationToken cancellationToken) =>
                Task.FromResult(store.Existing is { } existing && existing.WalletId == walletId && existing.IdempotencyKey == key
                    ? existing : null);

            public Task SaveAsync(Withdrawal withdrawal, OutboxMessage outbox, CancellationToken cancellationToken)
            {
                store.WriteAttempts++;
                if (store.SaveError is { } failure) throw failure;
                receipt = withdrawal;
                message = outbox;
                return Task.CompletedTask;
            }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                if (store.CommitError is { } failure) throw failure;
                store.Wallet!.BalanceMinor = workingWallet!.BalanceMinor;
                store.StoredReceipt = receipt;
                store.StoredEvent = message;
                store.Committed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                store.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
