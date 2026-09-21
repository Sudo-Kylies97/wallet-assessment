using Microsoft.EntityFrameworkCore;

namespace Wallet.Api.Data;

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    public DbSet<WalletAccount> Wallets => Set<WalletAccount>();
    public DbSet<Withdrawal> Withdrawals => Set<Withdrawal>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<WalletAccount>(entity =>
        {
            entity.ToTable("Wallets", table => table.HasCheckConstraint("CK_Wallet_Balance", "\"BalanceMinor\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Currency).HasMaxLength(3);
        });
        model.Entity<Withdrawal>(entity =>
        {
            entity.ToTable("Withdrawals", table =>
            {
                table.HasCheckConstraint("CK_Withdrawal_Amount", "\"AmountMinor\" > 0");
                table.HasCheckConstraint("CK_Withdrawal_Balance", "\"BalanceAfterMinor\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WalletId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<WalletAccount>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Currency).HasMaxLength(3);
        });
        model.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.Property(x => x.EventType).HasMaxLength(100);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.HasIndex(x => x.WithdrawalId).IsUnique();
            entity.HasOne<Withdrawal>().WithMany().HasForeignKey(x => x.WithdrawalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.NextAttemptAtUtc).HasFilter("\"PublishedAtUtc\" IS NULL");
        });
    }
}
