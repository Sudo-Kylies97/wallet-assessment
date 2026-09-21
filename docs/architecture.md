# withdrawal flow

```mermaid
sequenceDiagram
    participant C as Swagger client
    participant A as API / WithdrawalService
    participant D as PostgreSQL
    participant W as Outbox worker
    participant R as RabbitMQ
    C->>A: POST amountMinor + Idempotency-Key
    A->>A: Validate input
    A->>D: BEGIN transaction
    A->>D: SELECT wallet FOR UPDATE
    A->>D: Find receipt by wallet + key
    alt Key already succeeded
        D-->>A: Original receipt
        A->>D: End transaction and release lock
        A-->>C: Original 200 response (409 if amount differs)
    else New withdrawal
        A->>A: Check sufficient funds
        A->>D: Update balance and insert receipt + outbox event
        A->>D: COMMIT
        A-->>C: 200 receipt
    end
    loop Poll due events
        W->>D: Read unpublished outbox rows
        W->>R: Publish persistent event with mandatory routing
        alt Confirmed and routed
            R-->>W: Publisher confirm
            W->>D: Set PublishedAtUtc
        else Broker failure / returned message / timeout
            W->>D: Record attempt and next retry time
        end
    end
```

Insufficient funds, missing wallets, or database errors exit the transaction without committing. Each successful withdrawal has a unique `(WalletId, IdempotencyKey)` and exactly one stored outbox record, enforced by unique database indexes. This does not imply exactly one broker delivery.

A failure between broker confirmation and `PublishedAtUtc` being saved leaves a pending event. The next cycle publishes it again with the same event ID. A future consumer must record processed event IDs atomically with its own effects and acknowledge only after that commit.
