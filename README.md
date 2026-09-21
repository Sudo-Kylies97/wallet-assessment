# Wallet assessment

A small ASP.NET Core API for reading a wallet balance and withdrawing funds. PostgreSQL protects the balance and stores withdrawal receipts and pending events in one transaction. A background worker publishes committed events to RabbitMQ. **Swagger UI is the interactive client**; there is no separate frontend application.

## Run locally

Prerequisite: Docker with Compose v2, running Linux containers.

```sh
docker compose up --build
```

- Swagger UI: <http://localhost:8080/swagger>
- OpenAPI document: <http://localhost:8080/swagger/v1/swagger.json>
- RabbitMQ management: <http://localhost:15673> — username `wallet`, password `wallet_dev_password`
- PostgreSQL: `localhost:5433`, database/user `wallet`, password `wallet_dev_password`

The credentials are local development defaults. Compose binds exposed ports to loopback. The API automatically applies the checked-in EF Core migration, then inserts the seed wallet only if absent. PostgreSQL must be healthy before the API starts; RabbitMQ can start later without blocking withdrawals.

**Seed wallet:** `11111111-1111-1111-1111-111111111111`, **ZAR 1,000.00** (`100000` cents).

### Demonstrate with Swagger

1. Expand `GET /api/wallets/{walletId}/balance`, click **Try it out**, enter the seed wallet ID and execute. Expect `balanceMinor: 100000` on a fresh database.
2. Expand the withdrawal operation. Use the same wallet ID, a new UUID for `Idempotency-Key`, and `{ "amountMinor": 2500 }`. Execute: expect `200` and `balanceAfterMinor: 97500`.
3. Execute the identical request again: the response is the same receipt, and the balance is unchanged. Use a new key for each genuinely new withdrawal. The supplied example key is intentionally reusable for demonstrating this behaviour.
4. Change the amount while keeping that key: expect `409 idempotency_conflict`. Try an amount greater than the balance with a new key: expect `409 insufficient_funds`. Zero and fractional cents return `400`.
5. Open RabbitMQ management → **Queues and Streams** → `wallet.withdrawals` → **Get messages** to inspect the event. The queue appears after the first publication attempt. Use requeue mode if you want to leave the message available for inspection.

Swagger does not automatically manage retries. After a network error, `500`, or `503`, retry with the **same key and amount**: the original transaction may already have committed. A repeated successful request returns its original historical balance; use GET to read the current balance.

### Equivalent HTTP requests

```sh
curl http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/balance

curl -i -X POST http://localhost:8080/api/wallets/11111111-1111-1111-1111-111111111111/withdrawals \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' \
  -d '{"amountMinor":2500}'
```

### Stop, restart, and reset

```sh
docker compose down
docker compose up -d
```

Named volumes retain balances, receipts, outbox records, and RabbitMQ data. To deliberately delete **this application's data** and start from the seed again:

```sh
docker compose down -v
docker compose up --build
```

### Run with a local SDK

Install .NET SDK **10.0.401** (the version in `global.json`) or an allowed patch in that feature band. Then:

```sh
docker compose up -d postgres rabbitmq
dotnet restore Wallet.slnx --locked-mode
dotnet run --project src/Wallet.Api --urls http://localhost:8080
```

Configuration is in `src/Wallet.Api/appsettings.json`. Environment variables `ConnectionStrings__Wallet` and `RabbitMq__ConnectionString` override database and broker addresses. `Outbox__Enabled=false` disables background publication for controlled testing; leave it enabled in normal operation.

## API contract

All amounts are **integer ZAR cents**, represented by C# `long` and PostgreSQL `bigint`. The API accepts amounts from `1` to `9007199254740991` (JavaScript's maximum safe integer), subject to available funds. It rejects numeric strings, fractional values, omitted amounts, and unknown request fields. No rounding takes place.

| Operation | Success response |
|---|---|
| `GET /api/wallets/{walletId}/balance` | `200`: `walletId`, `currency`, `balanceMinor` |
| `POST /api/wallets/{walletId}/withdrawals` | `200`: `withdrawalId`, `walletId`, `amountMinor`, `balanceAfterMinor`, `currency`, `occurredAtUtc` |

POST requires a non-empty hyphenated UUID `Idempotency-Key` header. Keys are scoped to a wallet and retained indefinitely with successful receipts. A failed validation or insufficient-funds attempt does not reserve its key. All timestamps use UTC; receipts use PostgreSQL's microsecond precision so first responses and replays match.

Errors use `application/problem+json` with `status`, `title`, `code`, and `traceId`. Validation responses also contain `errors`.

| Status | Code | Meaning |
|---|---|---|
| 400 | `invalid_request` | Invalid body, missing header, invalid wallet UUID, or out-of-range amount |
| 400 | `invalid_idempotency_key` | Header is not a non-empty hyphenated UUID |
| 404 | `wallet_not_found` | Wallet does not exist |
| 409 | `insufficient_funds` | Balance cannot cover the requested amount |
| 409 | `idempotency_conflict` | A successful request used the same key with a different amount |
| 503 | `database_unavailable` | Transient database connection failure or timeout |
| 500 | `internal_error` | Unexpected server failure; internal details are logged, not returned |

## Technical choices and trade-offs

- **ASP.NET Core controllers:** standard binding, validation, Problem Details and Swagger integration. The controller handles HTTP, `WithdrawalService` handles the transaction, and `WalletDbContext` defines storage constraints. No generic repository or mediator framework is needed for two operations.
- **PostgreSQL and EF Core:** explicit migrations, relational constraints, and real transactional locking. The service acquires `SELECT ... FOR UPDATE` on the wallet before checking the key and balance. Requests for a wallet serialize; requests for different wallets can proceed independently. A database check constraint additionally prevents a negative stored balance. An unprotected read-then-write would allow overspending, and an in-memory lock would not protect separate processes.
- **Transactional outbox:** debit, receipt, and event commit together. Publishing directly inside the HTTP request was rejected: the database and broker cannot be committed atomically, and broker outages would either lose events or unnecessarily block withdrawals.
- **RabbitMQ:** durable direct exchange `wallet.events`, durable queue `wallet.withdrawals`, routing key `wallet.withdrawal.succeeded.v1`, persistent messages, mandatory routing, and publisher confirms. The worker records publication only after confirmation. Returned/unroutable messages are failures, even if the exchange accepted the publish.
- **One worker and bounded batches:** each cycle reads up to 20 due events, with a ten-second publication timeout per event. Failures retry after 2, 4, 8, 16, 32, then 60 seconds, capped at 60. The worker polls every second and retries indefinitely. Errors and attempts are persisted; a database outage is retried on subsequent cycles. One broker connection per event simplifies recovery at this low volume at the cost of throughput.
- **At-least-once events:** a crash or database failure after broker confirmation can cause the same event to be published again. Its event ID remains unchanged. A future consumer must deduplicate by event ID; there is no exactly-once delivery claim. Ordering is not guaranteed because delayed events can be overtaken.
- **Swagger client:** meets the demonstration need without a separate UI build or state management. Users explicitly supply and retain idempotency keys.

See [architecture and sequence diagrams](docs/architecture.md).

The local PostgreSQL connection disables GSS encryption negotiation because the demo uses password authentication and has no Kerberos setup. This avoids an unnecessary native Kerberos library dependency in the runtime container.

## Tests

With the SDK and Docker running:

```sh
dotnet test Wallet.slnx -c Release
```

If Docker Desktop reports an invalid socket bind mount for Testcontainers' cleanup container, run with `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. This is the socket path inside Docker's VM, which can differ from the host socket used by the .NET client.

Without a host SDK:

```sh
docker compose -f compose.tests.yaml run --build --rm tests
```

The test runner mounts the local Docker socket so Testcontainers can create disposable PostgreSQL and RabbitMQ instances. It does not use or reset the demonstration database. For a nonstandard socket, set `DOCKER_SOCKET_PATH` to its host path. Results from the container command are written to `artifacts/test-results/wallet-tests.trx`. GitHub Actions runs the same .NET suite and uploads results.

Tests cover positive seeding and restart persistence, exact-balance withdrawals, invalid input, missing wallets, insufficient funds, concurrent overspending attempts, sequential/concurrent idempotency, database constraints and outages, transactional rollback on failed event insertion, broker outage/recovery, event schema and persistence properties, unroutable messages, duplicate publication after a confirmation-save failure, retry delays, and Swagger's documented contract. They use actual database transactions and broker publishes, not EF's in-memory provider.

Local verification completed with **35 passing tests** through both the host SDK and the containerised Release test runner. The solution built without warnings or errors, and EF reported no pending model changes. A Playwright browser check exercised Swagger's balance, withdrawal, validation, conflict, and lost-response retry flows. Two successful demonstration withdrawals produced two published outbox records and two queued messages. GitHub Actions is configured; no remote CI result is claimed here.

To maintain the schema, run `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/Wallet.Api --output-dir Data/Migrations` after changing the EF model; commit the generated migration and snapshot.

### Broker-outage demonstration

1. Run `docker compose stop rabbitmq`.
2. Make a new withdrawal in Swagger. It still succeeds; logs show publication retries.
3. Run `docker compose start rabbitmq`. Within the retry interval (up to 60 seconds, plus processing time), the event is published.
4. Inspect attempts and timestamps when needed:

```sh
docker compose exec postgres psql -U wallet -d wallet -c 'SELECT "Id", "AttemptCount", "PublishedAtUtc", "NextAttemptAtUtc", "LastError" FROM "OutboxMessages";'
docker compose logs api
```
