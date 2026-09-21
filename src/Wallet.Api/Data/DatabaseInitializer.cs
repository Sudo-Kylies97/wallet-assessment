using Microsoft.EntityFrameworkCore;

namespace Wallet.Api.Data;

public static class DatabaseInitializer
{
    public static readonly Guid SeedWalletId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static async Task InitializeAsync(WalletDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
        // Never overwrite an existing balance on application restart.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Wallets" ("Id", "Currency", "BalanceMinor")
            VALUES ({SeedWalletId}, {"ZAR"}, {100000L})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
    }
}
