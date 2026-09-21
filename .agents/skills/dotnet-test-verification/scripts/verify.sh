#!/usr/bin/env bash
set -euo pipefail
repo_dir="${1:?Usage: verify.sh <repository>}"
sdk_bin="${DOTNET_BIN:-dotnet}"
cd "$repo_dir"
"$sdk_bin" --version
docker info --format '{{.ServerVersion}}'
"$sdk_bin" restore Wallet.slnx --locked-mode
"$sdk_bin" test Wallet.slnx -c Release --no-restore \
  --logger 'trx;LogFileName=wallet-tests.trx' \
  --results-directory artifacts/skill-review/test-results
