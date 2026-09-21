using System.Text.Json;

namespace Wallet.Api.Tests;

[Collection("Infrastructure")]
public sealed class SwaggerTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Swagger_describes_required_body_money_bounds_and_actual_problem_format()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var paths = document.RootElement.GetProperty("paths");
        var withdrawal = paths.GetProperty("/api/wallets/{walletId}/withdrawals").GetProperty("post");
        Assert.True(withdrawal.GetProperty("requestBody").GetProperty("required").GetBoolean());
        var balance = paths.GetProperty("/api/wallets/{walletId}/balance").GetProperty("get");
        foreach (var operation in new[] { balance, withdrawal })
        {
            foreach (var code in new[] { "400", "404", "500", "503" })
            {
                var mediaTypes = operation.GetProperty("responses").GetProperty(code).GetProperty("content");
                Assert.Single(mediaTypes.EnumerateObject());
                Assert.True(mediaTypes.TryGetProperty("application/problem+json", out _));
            }
        }
        Assert.True(withdrawal.GetProperty("responses").GetProperty("409").GetProperty("content")
            .TryGetProperty("application/problem+json", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var amount = schemas.GetProperty("WithdrawalRequest").GetProperty("properties").GetProperty("amountMinor");
        Assert.Equal("integer", amount.GetProperty("type").GetString());
        Assert.Equal(1, amount.GetProperty("minimum").GetInt64());
        Assert.Equal(Features.Money.MaxSafeInteger, amount.GetProperty("maximum").GetInt64());
        var problem = schemas.GetProperty("ProblemDetails").GetProperty("properties");
        Assert.True(problem.TryGetProperty("code", out _));
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.True(problem.TryGetProperty("errors", out _));
    }

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
