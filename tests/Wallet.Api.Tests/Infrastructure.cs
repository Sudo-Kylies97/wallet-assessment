using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wallet.Api.Data;
using Wallet.Api.Messaging;

namespace Wallet.Api.Tests;

[CollectionDefinition("Infrastructure", DisableParallelization = true)]
public sealed class InfrastructureCollection : ICollectionFixture<Infrastructure>;

public sealed class Infrastructure : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17.6-bookworm").Build();
    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();

    public Task InitializeAsync() => Task.WhenAll(Postgres.StartAsync(), Rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await Rabbit.DisposeAsync();
        await Postgres.DisposeAsync();
    }

    public async Task<TestApp> CreateAppAsync(bool enableOutbox = false)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(Postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = name };
        return new TestApp(builder.ConnectionString, Rabbit.GetConnectionString(), enableOutbox);
    }

    public async Task<IConnection> ConnectRabbitAsync() =>
        await new ConnectionFactory { Uri = new Uri(Rabbit.GetConnectionString()) }.CreateConnectionAsync();

    public async Task PurgeQueueAsync()
    {
        await using var connection = await ConnectRabbitAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(RabbitMqPublisher.Queue, true, false, false);
        await channel.QueuePurgeAsync(RabbitMqPublisher.Queue);
    }
}

public sealed class TestApp(string connectionString, string rabbitConnectionString, bool enableOutbox = false) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Wallet", ConnectionString);
        builder.UseSetting("RabbitMq:ConnectionString", rabbitConnectionString);
        builder.UseSetting("Outbox:Enabled", enableOutbox.ToString());
        builder.UseSetting("Logging:LogLevel:Default", "Critical");
    }

    public async Task WithDbAsync(Func<WalletDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<WalletDbContext>());
    }

    public async Task DispatchAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync(CancellationToken.None);
    }

    public Task MakeEventsDueAsync() => WithDbAsync(db => db.OutboxMessages
        .ExecuteUpdateAsync(update => update.SetProperty(x => x.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1))));
}
