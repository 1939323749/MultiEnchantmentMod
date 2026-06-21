#!/usr/bin/env bash
# End-to-end smoke test for the Axiom telemetry ingest path.
# Sends one event with the exact URL / headers / body shape that
# Telemetry/TelemetryReporter.cs uses, then reports Axiom's response.
#
# Usage:
#   AXIOM_TOKEN=xaat-... [AXIOM_DATASET=multienchantmentmod] [AXIOM_DOMAIN=https://api.axiom.co] \
#     ./scripts/axiom-smoke-test.sh
#
# Success looks like: HTTP 200  {"ingested":1,"failed":0,...}
set -euo pipefail

: "${AXIOM_TOKEN:?set AXIOM_TOKEN (xaat-... with ingest permission)}"
AXIOM_DATASET="${AXIOM_DATASET:-multienchantmentmod}"
AXIOM_DOMAIN="${AXIOM_DOMAIN:-https://api.axiom.co}"

url="${AXIOM_DOMAIN}/v1/datasets/${AXIOM_DATASET}/ingest"
now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
body="[{\"_time\":\"${now}\",\"event\":\"smoke_test\",\"distinct_id\":\"00000000-0000-0000-0000-000000000000\",\"source\":\"axiom-smoke-test.sh\"}]"

echo "POST ${url}"
code="$(printf '%s' "$body" | curl -sS -o /tmp/axiom_smoke_resp.txt -w '%{http_code}' \
  -X POST "$url" \
  -H "Authorization: Bearer ${AXIOM_TOKEN}" \
  -H "Content-Type: application/json" \
  --data-binary @-)"

echo "HTTP ${code}"
cat /tmp/axiom_smoke_resp.txt; echo

if [ "$code" -ge 200 ] && [ "$code" -lt 300 ]; then
  echo "OK — event ingested. Check the '${AXIOM_DATASET}' dataset Stream view in Axiom."
else
  echo "FAILED — fix the token/dataset/domain and retry."
  exit 1
fi
