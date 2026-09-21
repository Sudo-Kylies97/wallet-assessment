using Wallet.Api.Messaging;

namespace Wallet.Api.Tests.Unit;

[Trait("Category", "Unit")]
public sealed class OutboxRetryTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(100, 60)]
    public void Backoff_is_exponential_and_capped(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxDispatcher.RetryDelay(attempt));
}
