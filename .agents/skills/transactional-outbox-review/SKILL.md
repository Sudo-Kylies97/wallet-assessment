---
name: transactional-outbox-review
description: Review database-to-broker consistency in an existing transactional outbox, including wallet concurrency, idempotency, routing, confirms, and recovery. Use for requested correctness reviews of withdrawal and event-delivery code.
---

# Transactional outbox review

Trace the real request path from binding to transaction commit and publication. Read the service, EF model and migration, publisher, dispatcher, worker, and related tests. Treat README claims as hypotheses to compare against code.

Establish these invariants with source pointers:

- Balance checks and debits serialize on the wallet in the database; any idempotency lookup affected by concurrent requests occurs after acquiring that protection.
- A replay returns the original receipt and cannot create a second debit or outbox row. Conflicting reuse is rejected.
- The debit, receipt, and pending event share one database transaction; failed saves roll them all back.
- Publication uses persistent messages, durable routing, mandatory-return handling, and broker confirms. Confirm receipt alone is not proof that a consumer processed the event.
- A publication-state save failure leaves a retryable event with the same event ID; delivery may duplicate. Shutdown cancellation must not mark an unconfirmed event published.
- Retry scheduling and publisher ownership match the declared deployment assumptions. Do not demand leases for an explicitly single-publisher demo, but flag undocumented scaling claims.

Use existing fault-injection tests to verify failure windows. Never stop shared services or consume demonstration messages merely to perform a review; use isolated fixtures for dynamic tests. Separate confirmed defects, untested cases, and documented limitations. Report severity, location, a concrete failure scenario, and a proportionate suggestion. Do not edit application code during a review-only assignment.
