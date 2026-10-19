#!/usr/bin/env bash
# End-to-end check through the real gateway, queues and workers.
# Usage: scripts/smoke.sh http://localhost:8080 [demo-key]
set -euo pipefail
BASE="${1:-http://localhost:8080}"
KEY="${2:-}"
H=(-H "Content-Type: application/json")
[ -n "$KEY" ] && H+=(-H "X-Demo-Key: $KEY")
HOSP="FAC-LA-00231"
api() { curl -fsS "${H[@]}" "$@"; }
wait_for() { # wait_for <description> <command that prints something non-empty when done>
  for _ in $(seq 1 60); do out=$(eval "$2" || true); if [ -n "$out" ] && [ "$out" != "null" ]; then echo "  ok: $1"; return 0; fi; sleep 1; done
  echo "  FAILED waiting for: $1"; exit 1
}

echo "Waiting for the app"
for _ in $(seq 1 90); do curl -fsS "$BASE/health" >/dev/null 2>&1 && break; sleep 2; done
api "$BASE/api/meta"; echo

echo "1. Eligibility check (portal payer, answered from the member list)"
CORR=$(api -X POST "$BASE/api/hospitals/$HOSP/eligibility" -d '{"lookup":"52937108264"}' | jq -r .correlationId)
wait_for "cover is active" "api $BASE/api/eligibility/$CORR | jq -r 'select(.status==\"active\") | .status'"

echo "2. Pre-authorisation to Northstar HMO (portal)"
ID=$(api -X POST "$BASE/api/hospitals/$HOSP/preauth" -d '{"memberNo":"NST-55120-01","consent":true,"diagnosisCode":"R51","lines":[{"code":"CONS-01","amount":15000},{"code":"LAB-12","amount":6500}]}' | jq -r .id)
wait_for "request reached the payer inbox" "api $BASE/api/claims/$ID | jq -r 'select(.awaitingPayer==true) | .authNo'"

echo "3. Reviewer approves one line and queries the other"
CLAIM=$(api "$BASE/api/claims/$ID")
L1=$(echo "$CLAIM" | jq -r '.items[0].id'); L2=$(echo "$CLAIM" | jq -r '.items[1].id')
api -X POST "$BASE/api/claims/$ID/review" -d "{\"decisions\":[{\"lineId\":\"$L1\",\"decision\":\"approve\"},{\"lineId\":\"$L2\",\"decision\":\"query\",\"docType\":\"Lab result\",\"note\":\"Attach the signed request form\"}],\"remark\":\"Consultation approved.\"}"
wait_for "hospital sees the query" "api $BASE/api/claims/$ID | jq -r 'select(.rollup==\"query\") | .rollup'"

echo "4. Hospital uploads a document"
printf '%%PDF-1.4 smoke test' > /tmp/form.pdf
curl -fsS ${KEY:+-H "X-Demo-Key: $KEY"} -F "note=Signed form attached" -F "item-$L2=@/tmp/form.pdf;type=application/pdf" "$BASE/api/claims/$ID/documents"; echo
wait_for "documents back in the payer inbox" "api $BASE/api/claims/$ID | jq -r 'select(.awaitingPayer==true and (.items[1].attachments|length)==1) | .authNo'"
api -X POST "$BASE/api/claims/$ID/review" -d "{\"decisions\":[{\"lineId\":\"$L2\",\"decision\":\"approve\"}]}"
wait_for "pre-authorisation approved" "api $BASE/api/claims/$ID | jq -r 'select(.rollup==\"approved\") | .rollup'"

echo "5. Final claim, approval and ledger"
api -X POST "$BASE/api/claims/$ID/final"
wait_for "final claim in payer inbox" "api $BASE/api/claims/$ID | jq -r 'select(.awaitingPayer==true and .stage==\"claim\") | .stage'"
api -X POST "$BASE/api/claims/$ID/final-decision" -d '{"approve":true}'
wait_for "ledger entry created" "api $BASE/api/claims/$ID | jq -r '.ledger.amount // empty'"

echo "6. Batch payment and signed processor webhook"
AUTH=$(api "$BASE/api/claims/$ID" | jq -r .authNo)
BATCH=$(api -X POST "$BASE/api/payers/HMO-0XB/batches" -d "{\"hospitalCode\":\"$HOSP\",\"authNos\":[\"$AUTH\"]}" | jq -r .batchNo)
api -X POST "$BASE/api/batches/$BATCH/simulate-payment"
wait_for "claim marked paid" "api $BASE/api/claims/$ID | jq -r 'select(.ledger.status==\"paid\") | .ledger.status'"

echo "7. API payer decides automatically (Sapphire HMO)"
ID2=$(api -X POST "$BASE/api/hospitals/$HOSP/preauth" -d '{"memberNo":"SAP-22041-01","consent":true,"diagnosisCode":"K35.8","lines":[{"code":"SUR-12","amount":350000}]}' | jq -r .id)
wait_for "decided at tariff" "api $BASE/api/claims/$ID2 | jq -r 'select(.rollup==\"partial\") | .approvedTotal'"

echo "8. API payer offline: the switch retries, then delivers"
api -X POST "$BASE/api/switch/participants/HMO-0XA/online" -d '{"online":false}'
ID3=$(api -X POST "$BASE/api/hospitals/$HOSP/preauth" -d '{"memberNo":"SAP-22041-01","consent":true,"diagnosisCode":"B54","lines":[{"code":"CONS-01","amount":15000}]}' | jq -r .id)
wait_for "message retrying" "api $BASE/api/claims/$ID3 | jq -r 'select(.delivery==\"retrying\") | .delivery'"
api -X POST "$BASE/api/switch/participants/HMO-0XA/online" -d '{"online":true}'
wait_for "delivered after retry and decided" "api $BASE/api/claims/$ID3 | jq -r 'select(.rollup==\"approved\") | .rollup'"

echo "9. NHIA summary"
api "$BASE/api/nhia/summary" | jq '{totalClaims, liveClaims, approvalRate, medianHours}'
echo "All smoke checks passed."
