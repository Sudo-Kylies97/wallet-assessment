using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Messaging;

namespace Wallet.Api.Tests;

[Collection("Infrastructure")]
public sealed class EventTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Hosted_worker_publishes_without_manual_dispatch_or_schedule_changes()
    {
        await infrastructure.PurgeQueueAsync();
        await using var app = await infrastructure.CreateAppAsync(enableOutbox: true);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await WalletTests.WithdrawAsync(client, 2500)).StatusCode);

        var published = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!published)
        {
            await app.WithDbAsync(async db => published = await db.OutboxMessages
                .AnyAsync(x => x.PublishedAtUtc != null, deadline.Token));
            if (!published) await Task.Delay(100, deadline.Token);
        }
        await using var connection = await infrastructure.ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true);
        Assert.NotNull(delivery);
        await app.WithDbAsync(async db =>
        {
            var message = await db.OutboxMessages.SingleAsync();
            Assert.Equal(message.Id.ToString(), delivery.BasicProperties.MessageId);
            Assert.Equal(97500, (await db.Wallets.SingleAsync()).BalanceMinor);
        });
    }

    [Fact]
    public async Task Confirmed_event_has_expected_schema_routing_and_persistence()
    {
        await infrastructure.PurgeQueueAsync();
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await WalletTests.WithdrawAsync(client, 2500)).StatusCode);
        await app.DispatchAsync();

        await using var connection = await infrastructure.ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true);
        Assert.NotNull(delivery);
        Assert.Equal(RabbitMqPublisher.Exchange, delivery.Exchange);
        Assert.Equal(WithdrawalSucceeded.Type, delivery.RoutingKey);
        Assert.True(delivery.BasicProperties.Persistent);
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        var payload = JsonSerializer.Deserialize<WithdrawalSucceeded>(delivery.Body.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(delivery.BasicProperties.MessageId, payload.EventId.ToString());
        Assert.Equal(WithdrawalSucceeded.Type, payload.EventType);
        Assert.Equal(2500, payload.AmountMinor);
        Assert.Equal(97500, payload.BalanceAfterMinor);
        Assert.Equal("ZAR", payload.Currency);
        Assert.Equal(TimeSpan.Zero, payload.OccurredAtUtc.Offset);
        await app.WithDbAsync(async db =>
        {
            var message = await db.OutboxMessages.SingleAsync();
            Assert.NotNull(message.PublishedAtUtc);
            Assert.Equal(1, message.AttemptCount);
            Assert.Null(message.LastError);
            Assert.Equal(payload.WithdrawalId, message.WithdrawalId);
        });
        await app.DispatchAsync();
        Assert.Null(await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true));
    }

    [Fact]
    public async Task Broker_outage_does_not_block_withdrawal_and_pending_event_survives_restart()
    {
        await infrastructure.PurgeQueueAsync();
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        await infrastructure.Rabbit.StopAsync();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await WalletTests.WithdrawAsync(client, 2500)).StatusCode);
            await app.DispatchAsync();
            await app.WithDbAsync(async db =>
            {
                Assert.Equal(97500, (await db.Wallets.SingleAsync()).BalanceMinor);
                var message = await db.OutboxMessages.SingleAsync();
                Assert.Null(message.PublishedAtUtc);
                Assert.Equal(1, message.AttemptCount);
                Assert.NotNull(message.LastError);
                Assert.True(message.NextAttemptAtUtc > message.CreatedAtUtc);
            });
        }
        finally { await infrastructure.Rabbit.StartAsync(); }

        await using var restarted = new TestApp(app.ConnectionString, infrastructure.Rabbit.GetConnectionString());
        _ = restarted.CreateClient();
        await restarted.MakeEventsDueAsync();
        await restarted.DispatchAsync();
        await using var connection = await infrastructure.ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.NotNull(await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true));
        await restarted.WithDbAsync(async db => Assert.NotNull((await db.OutboxMessages.SingleAsync()).PublishedAtUtc));
    }

    [Fact]
    public async Task Unroutable_event_is_not_marked_published()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        await WalletTests.WithdrawAsync(client, 2500);
        await app.WithDbAsync(db => db.OutboxMessages.ExecuteUpdateAsync(update => update.SetProperty(x => x.EventType, "unbound.event")));
        await app.DispatchAsync();
        await app.WithDbAsync(async db =>
        {
            var message = await db.OutboxMessages.SingleAsync();
            Assert.Null(message.PublishedAtUtc);
            Assert.Equal("PublishReturnException", message.LastError);
        });
    }

    [Fact]
    public async Task Failure_after_confirm_republishes_same_event_id_without_a_second_debit()
    {
        await infrastructure.PurgeQueueAsync();
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        await WalletTests.WithdrawAsync(client, 2500);
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_confirmation() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected confirmation-save failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_confirmation BEFORE UPDATE ON "OutboxMessages"
            FOR EACH ROW EXECUTE FUNCTION reject_confirmation();
            """));
        await Assert.ThrowsAsync<DbUpdateException>(app.DispatchAsync);
        await app.WithDbAsync(async db =>
        {
            Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedAtUtc);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_confirmation ON \"OutboxMessages\"");
        });
        await app.DispatchAsync();
        await using var connection = await infrastructure.ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        var first = await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true);
        var second = await channel.BasicGetAsync(RabbitMqPublisher.Queue, autoAck: true);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.BasicProperties.MessageId, second.BasicProperties.MessageId);
        Assert.Equal(first.Body.ToArray(), second.Body.ToArray());
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(97500, (await db.Wallets.SingleAsync()).BalanceMinor);
            Assert.Single(await db.Withdrawals.ToListAsync());
        });
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(100, 60)]
    public void Backoff_is_exponential_and_capped(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxDispatcher.RetryDelay(attempt));
}
