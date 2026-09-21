using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;

namespace Wallet.Api.Messaging;

public sealed class OutboxDispatcher(WalletDbContext db, IEventPublisher publisher,
    TimeProvider clock, ILogger<OutboxDispatcher> logger)
{
    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var pending = await db.OutboxMessages
            .Where(x => x.PublishedAtUtc == null && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc).Take(20).ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            message.AttemptCount++;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await publisher.PublishAsync(message, timeout.Token);
                message.PublishedAtUtc = clock.GetUtcNow();
                message.LastError = null;
                logger.LogInformation("Published withdrawal event {EventId}", message.Id);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                message.NextAttemptAtUtc = clock.GetUtcNow().Add(RetryDelay(message.AttemptCount));
                message.LastError = exception.GetType().Name;
                logger.LogWarning(exception, "Event {EventId} publication failed on attempt {Attempt}; retry at {NextAttempt}",
                    message.Id, message.AttemptCount, message.NextAttemptAtUtc);
            }
            // If this save fails after a broker ack, the event remains pending and may be published again.
            await db.SaveChangesAsync(cancellationToken);
        }
        return pending.Count;
    }

    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));
}
