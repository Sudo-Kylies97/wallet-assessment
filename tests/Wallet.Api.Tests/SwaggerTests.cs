using System.Text.Json;

namespace Wallet.Api.Tests;

[Collection("Infrastructure")]
public sealed class SwaggerTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Swagger_is_the_interactive_client_and_describes_required_idempotency_header()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("swagger-ui", html);
        using var specification = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = specification.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/wallets/{walletId}/balance", out _));
        var withdrawal = paths.GetProperty("/api/wallets/{walletId}/withdrawals").GetProperty("post");
        var header = withdrawal.GetProperty("parameters").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", header.GetProperty("in").GetString());
        Assert.True(header.GetProperty("required").GetBoolean());
        Assert.Contains("11111111-1111-1111-1111-111111111111", specification.RootElement.GetRawText());
        Assert.True(withdrawal.GetProperty("responses").TryGetProperty("409", out _));
    }
}
