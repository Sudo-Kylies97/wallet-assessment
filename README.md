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

**Deployment assumption:** Run one API instance with one outbox publisher; multi-instance deployment is unsupported. The outbox has no claim or lease mechanism, so competing publishers can duplicate messages and overwrite retry counts. Each API instance also runs migrations and seeding at startup. Before scaling, coordinate outbox ownership and move database initialization to a single deployment step. Do not use `docker compose up --scale api=2`; the fixed host port also prevents that configuration.

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

## Tests

Fast unit tests require the pinned .NET SDK, but no Docker, PostgreSQL, or RabbitMQ:

```sh
dotnet test Wallet.slnx -c Release --filter Category=Unit
```

The service uses a narrow `IWithdrawalStore` transaction interface. Unit tests substitute a transactional test double and a fixed `TimeProvider` to check business outcomes, receipt replay, timestamp precision, and storage failures. PostgreSQL row locking and atomic rollback remain covered by integration tests. Retry-delay tests also run in the unit layer, outside the infrastructure fixture.

Run only the integration tests with Docker available:

```sh
dotnet test Wallet.slnx -c Release --filter Category=Integration
```

Run both layers with the SDK and Docker running:

```sh
dotnet test Wallet.slnx -c Release
```

If Docker Desktop reports an invalid socket bind mount for Testcontainers' cleanup container, run with `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. This is the socket path inside Docker's VM, which can differ from the host socket used by the .NET client.

Without a host SDK:

```sh
docker compose -f compose.tests.yaml run --build --rm tests
```

The test runner mounts the local Docker socket so Testcontainers can create disposable PostgreSQL and RabbitMQ instances. It does not use or reset the demonstration database. For a nonstandard socket, set `DOCKER_SOCKET_PATH` to its host path. Results from the container command are written to `artifacts/test-results/wallet-tests.trx`. GitHub Actions runs the same .NET suite and uploads results.

Integration tests cover positive seeding and restart persistence, exact-balance withdrawals, invalid input, missing wallets, insufficient funds, concurrent overspending attempts, sequential/concurrent idempotency, database constraints and outages, transactional rollback on failed event insertion, broker outage/recovery, event schema and persistence properties, unroutable messages, duplicate publication after a confirmation-save failure, and Swagger's documented contract. They use actual database transactions and broker publishes, not EF's in-memory provider.

Initial verification completed with **35 passing tests** through both the host SDK and the containerised Release test runner. The subsequent skill-driven review added two tests, and the updated host Release suite passed **37 tests**, with zero failures or skips. The solution built without warnings or errors, and EF reported no pending model changes. A Playwright browser check exercised Swagger's balance, withdrawal, validation, conflict, and lost-response retry flows. Two successful demonstration withdrawals produced two published outbox records and two queued messages. GitHub Actions is configured; no remote CI result is claimed here.

The unit-layer revision passed **52 tests: 20 unit and 32 integration**, with zero failures or skips, through the host Release runner using SDK 10.0.401. The unit-only command also passed with `DOCKER_HOST` pointing to a nonexistent socket, confirming that it does not start infrastructure fixtures. The containerised runner and browser checks were not rerun for this revision.

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

## AI-assisted development

1. **Bootstrap the project and database connection.** Codex scaffolded the ASP.NET Core solution, configured EF Core/Npgsql, and connected the API to PostgreSQL. It also checked the local tooling and installed the .NET 10 SDK needed to build and test the solution.

2. **Define the schema and persistence rules.** AI-assisted design covered wallets, withdrawal receipts, and outbox messages. This included representing money in integer cents, preventing negative balances with a database constraint, and enforcing unique idempotency keys per wallet. The resulting model and migration are in `src/Wallet.Api/Data`.

3. **Review withdrawal logic and suggest changes for concurrent requests.** Codex helped review how simultaneous withdrawals could read the same available balance and allow overspending. It suggested locking the wallet row before checking the balance and idempotency key, with the debit, receipt, and pending event saved in one transaction. Concurrent-request tests against PostgreSQL verified that competing withdrawals cannot overspend and duplicate requests do not debit the wallet twice.

4. **Build out Docker Compose.** Codex created the API Dockerfile and Compose services for PostgreSQL and RabbitMQ, including persistent volumes, local connection settings, and database readiness checks. It also added a separate containerised test runner so the suite can run without a host .NET SDK.

5. **Set up and run automated tests on my machine.** Codex configured xUnit and Testcontainers, generated unit and integration tests, and ran them locally and through Docker. It resolved a Docker Desktop socket-mount issue and adjusted restart tests for changing container ports. Both runs finished with 35 passing tests; most of the suite exercises real database and broker behaviour.

6. **Explore event-delivery failure scenarios.** AI-assisted reasoning informed the transactional outbox, mandatory RabbitMQ routing, publisher confirms, and retry backoff. Fault-injection tests deliberately rejected an outbox insert to check rollback, then rejected a publication-state update to demonstrate why an already-published event can be delivered again. These cases are covered in `WalletTests` and `EventTests`.

7. **Use test feedback to refine the implementation.** Compilation and runtime checks exposed issues that were corrected during development. Examples include ASP.NET Core validation attributes on a positional record, which led to a request class with property validation, and timestamp precision, which was aligned with PostgreSQL so original and replayed receipts match.


### Custom skills used for a subsequent review

| Skill package | How it was used |
|---|---|
| [`dotnet-test-verification`](.agents/skills/dotnet-test-verification/SKILL.md) | Checked the pinned SDK and Docker, restored locked dependencies, and ran the Release suite through its executable helper. Its coverage guidance also informed the test review. |
| [`transactional-outbox-review`](.agents/skills/transactional-outbox-review/SKILL.md) | Guided an independent review of wallet locking, idempotency, transaction boundaries, broker confirmation, duplicate delivery, and publisher ownership. |
| [`docker-local-validation`](.agents/skills/docker-local-validation/SKILL.md) | Validated both Compose configurations and inspected running service health, logs, non-root API execution, volume configuration, and read-only API connectivity. The review did not reset the demonstration data. |

To repeat the skill's automated test workflow from the repository root:

```sh
.agents/skills/dotnet-test-verification/scripts/verify.sh "$PWD"
```

`DOTNET_BIN` can select a particular SDK executable. On this machine, the run also used `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. Results are written to the ignored `artifacts/skill-review/test-results` directory.
