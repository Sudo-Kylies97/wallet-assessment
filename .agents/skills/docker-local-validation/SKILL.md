---
name: docker-local-validation
description: Validate an existing Docker Compose development setup for a .NET API, PostgreSQL, and RabbitMQ. Use for local startup, persistence, connectivity, or container-test setup reviews without resetting existing data.
---

# Docker local validation

Read both Compose files, Dockerfiles, ignore rules, SDK/package pins, and connection settings. Resolve project names before executing commands so unrelated Compose projects are not affected.

Validate Compose syntax, database readiness dependencies, service DNS names versus host ports, non-root API execution, named-volume mounts, and the test runner's Docker socket/host routing. Distinguish local demonstration credentials and loopback bindings from production configuration.

For a running stack, inspect container health, logs, Swagger, and the balance endpoint. Prefer GET requests and database reads; do not create withdrawals or acknowledge queued messages just to show connectivity. If startup/build is requested, use the documented command and report whether it used existing images or built from source.

Persistence claims require observed restart evidence or an isolated disposable scenario. Do not run `down -v`, delete volumes, or restart shared services for a review. When a restart test is appropriate, record the balance before and after and isolate its project/volumes from the demonstration stack.

Report exact checks and outcomes, version or port mismatches, and any untested claims. A healthy container does not by itself prove API correctness, and a source review is not a successful runtime check. Keep fixes limited to demonstrated problems within the user's requested scope.
