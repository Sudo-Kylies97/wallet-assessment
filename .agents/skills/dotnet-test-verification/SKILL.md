---
name: dotnet-test-verification
description: Verify an existing .NET solution's test setup and coverage using its pinned SDK, xUnit, and real Testcontainers dependencies. Use for requested test-readiness or regression reviews, not to scaffold unrelated applications.
---

# .NET test verification

Inspect `global.json`, project references, test fixtures, and documented commands before running anything. Identify the SDK requirement and whether tests create disposable dependencies or connect to shared services. Do not stop or reset the demonstration database to exercise failure paths.

Run `scripts/verify.sh <repository>` from this skill directory. Set `DOTNET_BIN` to a particular SDK executable if the default `dotnet` does not satisfy `global.json`. The script checks Docker, restores locked dependencies, and executes the Release suite, writing results beneath the ignored `artifacts/skill-review` directory. A failed command must remain a failure; diagnose it before retrying. Apply Docker socket overrides only when the environment requires them.

For coverage review, map wallet requirements to actual assertions, not test names or counts alone. Check concurrency, duplicate requests, exact-balance withdrawals, rollback, dependency outages, and API errors. Distinguish checks using real infrastructure from mocks and pure unit tests. Identify relevant missing cases without expanding product scope.

Report the command, SDK, passed/failed/skipped counts, environment fixes, and concrete gaps with source locations. A passing suite establishes only its tested behaviours. Do not claim tests were executed when only reading their source, or silently add packages/change versions to make the review pass.
