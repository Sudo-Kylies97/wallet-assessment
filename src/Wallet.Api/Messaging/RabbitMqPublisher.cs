using System.Text;
using RabbitMQ.Client;
using Wallet.Api.Data;

namespace Wallet.Api.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class RabbitMqPublisher(IConfiguration configuration) : IEventPublisher
{
    public const string Exchange = "wallet.events";
    public const string Queue = "wallet.withdrawals";

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // A fresh connection per event keeps recovery explicit for this low-volume demo.
        // A production publisher should reuse a connection and serialize channel access.
        var factory = new ConnectionFactory
        {
            Uri = new Uri(configuration["RabbitMq:ConnectionString"]!),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, WithdrawalSucceeded.Type,
            cancellationToken: cancellationToken);
        var properties = new BasicProperties
        {
            Persistent = true, ContentType = "application/json", MessageId = message.Id.ToString(),
            Type = message.EventType, Timestamp = new AmqpTimestamp(message.CreatedAtUtc.ToUnixTimeSeconds())
        };
        // With confirmation tracking enabled, this awaits the ack and throws on nack/basic.return.
        await channel.BasicPublishAsync(Exchange, message.EventType, mandatory: true,
            basicProperties: properties, body: Encoding.UTF8.GetBytes(message.Payload), cancellationToken);
    }
}
