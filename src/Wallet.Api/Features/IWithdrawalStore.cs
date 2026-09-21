using Wallet.Api.Data;

namespace Wallet.Api.Features;

// A narrow transaction boundary, not a general-purpose repository.
public interface IWithdrawalStore
{
    Task<IWithdrawalTransaction> BeginAsync(CancellationToken cancellationToken);
}

public interface IWithdrawalTransaction : IAsyncDisposable
{
    // Returns the tracked wallet and holds its database lock until this transaction ends.
    Task<WalletAccount?> LockWalletAsync(Guid walletId, CancellationToken cancellationToken);
    Task<Withdrawal?> FindWithdrawalAsync(Guid walletId, Guid key, CancellationToken cancellationToken);
    // Persists the tracked balance, receipt and event in this transaction.
    Task SaveAsync(Withdrawal withdrawal, OutboxMessage message, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}
