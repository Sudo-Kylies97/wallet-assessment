using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wallet.Api.Features;

namespace Wallet.Api.Data;

public sealed class PostgresWithdrawalStore(WalletDbContext db) : IWithdrawalStore
{
    public async Task<IWithdrawalTransaction> BeginAsync(CancellationToken cancellationToken) =>
        new Transaction(db, await db.Database.BeginTransactionAsync(cancellationToken));

    private sealed class Transaction(WalletDbContext db, IDbContextTransaction transaction) : IWithdrawalTransaction
    {
        public async Task<WalletAccount?> LockWalletAsync(Guid walletId, CancellationToken cancellationToken)
        {
            var wallets = await db.Wallets.FromSqlInterpolated($"""
                SELECT * FROM "Wallets" WHERE "Id" = {walletId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            return wallets.SingleOrDefault();
        }

        public Task<Withdrawal?> FindWithdrawalAsync(Guid walletId, Guid key, CancellationToken cancellationToken) =>
            db.Withdrawals.SingleOrDefaultAsync(x => x.WalletId == walletId && x.IdempotencyKey == key, cancellationToken);

        public async Task SaveAsync(Withdrawal withdrawal, OutboxMessage message, CancellationToken cancellationToken)
        {
            db.Withdrawals.Add(withdrawal);
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync(cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try { await transaction.DisposeAsync(); }
            finally
            {
                // Discard tracked mutations after rollback as well as after success. This scoped
                // context belongs to the withdrawal operation and may be reused by a non-HTTP caller.
                db.ChangeTracker.Clear();
            }
        }
    }
}
