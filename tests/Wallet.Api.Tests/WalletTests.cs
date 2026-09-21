using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;
using Wallet.Api.Features;

namespace Wallet.Api.Tests;

[Collection("Infrastructure")]
public sealed class WalletTests(Infrastructure infrastructure)
{
    internal static string WalletPath => $"/api/wallets/{DatabaseInitializer.SeedWalletId}";

    internal static Task<HttpResponseMessage> WithdrawAsync(HttpClient client, long amount,
        Guid? key = null, Guid? wallet = null) => SendAsync(client, $"{{\"amountMinor\":{amount}}}",
            (key ?? Guid.NewGuid()).ToString(), wallet);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string body, string? key, Guid? wallet = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/wallets/{wallet ?? DatabaseInitializer.SeedWalletId}/withdrawals")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Seed_is_positive_and_restart_does_not_reset_balance()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var initial = await client.GetFromJsonAsync<BalanceResponse>(WalletPath + "/balance");
        Assert.Equal(new BalanceResponse(DatabaseInitializer.SeedWalletId, "ZAR", 100000), initial);
        Assert.Equal(HttpStatusCode.OK, (await WithdrawAsync(client, 2500)).StatusCode);

        await using var restarted = new TestApp(app.ConnectionString, infrastructure.Rabbit.GetConnectionString());
        using var restartedClient = restarted.CreateClient();
        var balance = await restartedClient.GetFromJsonAsync<BalanceResponse>(WalletPath + "/balance");
        Assert.Equal(97500, balance!.BalanceMinor);
    }

    [Theory]
    [InlineData(2500, 97500)]
    [InlineData(100000, 0)]
    public async Task Successful_withdrawal_commits_balance_receipt_and_event(long amount, long expected)
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var response = await WithdrawAsync(client, amount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<WithdrawalResponse>();
        Assert.Equal(expected, receipt!.BalanceAfterMinor);
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(expected, (await db.Wallets.SingleAsync()).BalanceMinor);
            Assert.Equal(receipt.WithdrawalId, (await db.Withdrawals.SingleAsync()).Id);
            var message = await db.OutboxMessages.SingleAsync();
            Assert.Null(message.PublishedAtUtc);
            var payload = JsonSerializer.Deserialize<WithdrawalSucceeded>(message.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(receipt.WithdrawalId, payload!.WithdrawalId);
            Assert.Equal(message.Id, payload.EventId);
            Assert.Equal(amount, payload.AmountMinor);
        });
    }

    [Theory]
    [InlineData("{\"amountMinor\":0}")]
    [InlineData("{\"amountMinor\":-1}")]
    [InlineData("{\"amountMinor\":1.5}")]
    [InlineData("{\"amountMinor\":9007199254740992}")]
    [InlineData("{\"amountMinor\":9223372036854775808}")]
    [InlineData("{\"amountMinor\":\"2500\"}")]
    [InlineData("{\"amountMinor\":null}")]
    [InlineData("{\"amountMinor\":2500,\"unexpected\":true}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not-json")]
    public async Task Invalid_amount_returns_problem_details_and_changes_nothing(string body)
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var response = await SendAsync(client, body, Guid.NewGuid().ToString());
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        await AssertUnchangedAsync(app);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_key_is_rejected(string? key)
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var response = await SendAsync(client, "{\"amountMinor\":2500}", key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(app);
    }

    [Fact]
    public async Task Missing_wallet_returns_404_on_both_operations()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var id = Guid.NewGuid();
        await AssertProblemAsync(await client.GetAsync($"/api/wallets/{id}/balance"), HttpStatusCode.NotFound, "wallet_not_found");
        await AssertProblemAsync(await WithdrawAsync(client, 100, wallet: id), HttpStatusCode.NotFound, "wallet_not_found");
    }

    [Fact]
    public async Task Insufficient_funds_does_not_reserve_key_or_emit_event()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var key = Guid.NewGuid();
        await AssertProblemAsync(await WithdrawAsync(client, 100001, key), HttpStatusCode.Conflict, "insufficient_funds");
        await AssertUnchangedAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await WithdrawAsync(client, 100, key)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_withdrawals_cannot_overspend()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => WithdrawAsync(client, 30000)));
        Assert.Equal(3, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(10000, (await db.Wallets.SingleAsync()).BalanceMinor);
            Assert.Equal(3, await db.Withdrawals.CountAsync());
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
        });
    }

    [Fact]
    public async Task Concurrent_replays_return_identical_receipt_with_one_debit_and_event()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => WithdrawAsync(client, 2500, key)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var receipts = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
        Assert.Single(receipts.Distinct());
        await app.WithDbAsync(async db =>
        {
            Assert.Equal(97500, (await db.Wallets.SingleAsync()).BalanceMinor);
            Assert.Single(await db.Withdrawals.ToListAsync());
            Assert.Single(await db.OutboxMessages.ToListAsync());
        });
    }

    [Fact]
    public async Task Sequential_replay_returns_original_receipt_even_after_another_withdrawal()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        var key = Guid.NewGuid();
        var first = await (await WithdrawAsync(client, 2500, key)).Content.ReadAsStringAsync();
        await WithdrawAsync(client, 500);
        var replay = await WithdrawAsync(client, 2500, key);
        Assert.Equal(first, await replay.Content.ReadAsStringAsync());
        await AssertProblemAsync(await WithdrawAsync(client, 2600, key), HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(97000, (await client.GetFromJsonAsync<BalanceResponse>(WalletPath + "/balance"))!.BalanceMinor);
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_debit_and_receipt()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_outbox() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'injected outbox failure'; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_outbox BEFORE INSERT ON "OutboxMessages"
            FOR EACH ROW EXECUTE FUNCTION reject_outbox();
            """));
        await AssertProblemAsync(await WithdrawAsync(client, 2500), HttpStatusCode.InternalServerError, "internal_error");
        await AssertUnchangedAsync(app);
    }

    [Fact]
    public async Task Database_constraint_rejects_negative_balances()
    {
        await using var app = await infrastructure.CreateAppAsync();
        _ = app.CreateClient();
        await app.WithDbAsync(async db =>
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"Wallets\" SET \"BalanceMinor\" = -1")));
        await AssertUnchangedAsync(app);
    }

    [Fact]
    public async Task Database_outage_returns_503_without_exposing_connection_details()
    {
        await using var app = await infrastructure.CreateAppAsync();
        using var client = app.CreateClient();
        await infrastructure.Postgres.StopAsync();
        try
        {
            await AssertProblemAsync(await client.GetAsync(WalletPath + "/balance"), HttpStatusCode.ServiceUnavailable, "database_unavailable");
            await AssertProblemAsync(await WithdrawAsync(client, 2500), HttpStatusCode.ServiceUnavailable, "database_unavailable");
        }
        finally { await infrastructure.Postgres.StartAsync(); }
        // Docker can assign a new random host port when a test container restarts.
        var restoredConnection = new Npgsql.NpgsqlConnectionStringBuilder(infrastructure.Postgres.GetConnectionString())
        { Database = new Npgsql.NpgsqlConnectionStringBuilder(app.ConnectionString).Database };
        await using var restarted = new TestApp(restoredConnection.ConnectionString, infrastructure.Rabbit.GetConnectionString());
        await AssertUnchangedAsync(restarted);
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("stackTrace", out _));
    }

    private static Task AssertUnchangedAsync(TestApp app) => app.WithDbAsync(async db =>
    {
        Assert.Equal(100000, (await db.Wallets.SingleAsync()).BalanceMinor);
        Assert.Empty(await db.Withdrawals.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    });
}
