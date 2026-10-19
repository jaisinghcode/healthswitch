#!/usr/bin/env bash
# End-to-end check through the real gateway, queues and workers, signed in as each organisation.
# Usage: scripts/smoke.sh http://localhost:8080 [demo-key]
set -euo pipefail
BASE="${1:-http://localhost:8080}"
KEY="${2:-}"
KH=(); [ -n "$KEY" ] && KH=(-H "X-Demo-Key: $KEY")
PALM_CODE="FAC-LA-00231"; RIV_CODE="FAC-RI-00077"
fail() { echo "  FAILED: $1"; exit 1; }
login() {
  curl -fsS "${KH[@]}" -H "Content-Type: application/json" -X POST "$BASE/api/login" \
    -d "{\"portal\":\"$1\",\"userName\":\"$2\",\"password\":\"$3\"}" | jq -r .token
}
# call <token> <curl args...>
call() { local tok="$1"; shift; curl -fsS "${KH[@]}" -H "Authorization: Bearer $tok" -H "Content-Type: application/json" "$@"; }
code() { local tok="$1"; shift; curl -s -o /dev/null -w "%{http_code}" "${KH[@]}" ${tok:+-H "Authorization: Bearer $tok"} -H "Content-Type: application/json" "$@"; }
wait_for() { # wait_for <description> <command that prints something non-empty when done>
  for _ in $(seq 1 60); do out=$(eval "$2" || true); if [ -n "$out" ] && [ "$out" != "null" ]; then echo "  ok: $1"; return 0; fi; sleep 1; done
  fail "waiting for: $1"
}

echo "Waiting for the app"
for _ in $(seq 1 90); do curl -fsS "$BASE/health" >/dev/null 2>&1 && break; sleep 2; done
curl -fsS "$BASE/api/meta"; echo
for p in palmgrove riverside northstar sapphire switch nhia; do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/$p")" = "200" ] || fail "portal page /$p"
done
echo "  ok: all six portal pages are served"

echo "0. Sign in to each organisation's portal"
PALM=$(login palmgrove billing@palmgrove.ng palmgrove2026)
RIV=$(login riverside billing@riversidemedical.ng riverside2026)
NST=$(login northstar claims@northstarhmo.ng northstar2026)
SAP=$(login sapphire claims@sapphirehmo.ng sapphire2026)
OPS=$(login switch ops@healthswitch.ng switch2026)
REG=$(login nhia analyst@regulator.demo regulator2026)
[ "$(code "" -X POST "$BASE/api/login" -d '{"portal":"northstar","userName":"claims@northstarhmo.ng","password":"wrong"}')" = "401" ] || fail "wrong password refused"
[ "$(code "" "$BASE/api/claims")" = "401" ] || fail "claims need a login"
echo "  ok: six logins issued; wrong password and anonymous access refused"

echo "1. Eligibility check from Palmgrove (portal payer, answered from the member list)"
CORR=$(call "$PALM" -X POST "$BASE/api/hospitals/$PALM_CODE/eligibility" -d '{"lookup":"52937108264"}' | jq -r .correlationId)
wait_for "cover is active" "call $PALM $BASE/api/eligibility/$CORR | jq -r 'select(.status==\"active\") | .status'"

echo "2. Palmgrove sends a pre-authorisation to Northstar HMO"
ID=$(call "$PALM" -X POST "$BASE/api/hospitals/$PALM_CODE/preauth" -d '{"memberNo":"NST-55120-01","consent":true,"diagnosisCode":"R51","lines":[{"code":"CONS-01","amount":15000},{"code":"LAB-12","amount":6500}]}' | jq -r .id)
wait_for "request reached Northstar's inbox" "call $NST '$BASE/api/claims?awaiting=true' | jq -r '.[] | select(.id==\"$ID\") | .authNo'"

echo "   Isolation between organisations"
[ "$(code "$RIV" "$BASE/api/claims/$ID")" = "403" ] || fail "Riverside must not see Palmgrove's claim"
[ "$(code "$SAP" "$BASE/api/claims/$ID")" = "403" ] || fail "Sapphire must not see Northstar's claim"
[ "$(code "$RIV" -X POST "$BASE/api/hospitals/$PALM_CODE/preauth" -d '{}')" = "403" ] || fail "Riverside must not send as Palmgrove"
[ "$(code "$PALM" "$BASE/api/switch/overview")" = "403" ] || fail "hospitals cannot open the switch console"
[ "$(code "$PALM" "$BASE/api/nhia/summary")" = "403" ] || fail "hospitals cannot open the regulator view"
[ "$(call "$RIV" "$BASE/api/claims" | jq "[.[] | select(.hospitalCode != \"$RIV_CODE\")] | length")" = "0" ] || fail "Riverside list contains other hospitals"
[ "$(call "$SAP" "$BASE/api/claims" | jq '[.[] | select(.payerCode != "HMO-0XA")] | length')" = "0" ] || fail "Sapphire list contains other payers"
echo "  ok: each organisation only sees its own claims"

echo "3. Northstar approves one line and queries the other"
CLAIM=$(call "$NST" "$BASE/api/claims/$ID")
L1=$(echo "$CLAIM" | jq -r '.items[0].id'); L2=$(echo "$CLAIM" | jq -r '.items[1].id')
[ "$(code "$SAP" -X POST "$BASE/api/claims/$ID/review" -d '{"decisions":[]}')" = "403" ] || fail "Sapphire must not review Northstar's request"
call "$NST" -X POST "$BASE/api/claims/$ID/review" -d "{\"decisions\":[{\"lineId\":\"$L1\",\"decision\":\"approve\"},{\"lineId\":\"$L2\",\"decision\":\"query\",\"docType\":\"Lab result\",\"note\":\"Attach the signed request form\"}],\"remark\":\"Consultation approved.\"}"
wait_for "Palmgrove sees the query" "call $PALM $BASE/api/claims/$ID | jq -r 'select(.rollup==\"query\") | .rollup'"

echo "4. Palmgrove uploads a document"
printf '%%PDF-1.4 smoke test' > /tmp/form.pdf
curl -fsS "${KH[@]}" -H "Authorization: Bearer $PALM" -F "note=Signed form attached" -F "item-$L2=@/tmp/form.pdf;type=application/pdf" "$BASE/api/claims/$ID/documents"; echo
wait_for "documents back in Northstar's inbox" "call $NST $BASE/api/claims/$ID | jq -r 'select(.awaitingPayer==true and (.items[1].attachments|length)==1) | .authNo'"
ATT=$(call "$NST" "$BASE/api/claims/$ID" | jq -r '.items[1].attachments[0].id')
[ "$(code "" "$BASE/api/attachments/$ATT?token=$NST${KEY:+&key=$KEY}")" = "200" ] || fail "Northstar opens the document"
[ "$(code "" "$BASE/api/attachments/$ATT?token=$RIV${KEY:+&key=$KEY}")" = "403" ] || fail "Riverside must not open Palmgrove's document"
call "$NST" -X POST "$BASE/api/claims/$ID/review" -d "{\"decisions\":[{\"lineId\":\"$L2\",\"decision\":\"approve\"}]}"
wait_for "pre-authorisation approved" "call $PALM $BASE/api/claims/$ID | jq -r 'select(.rollup==\"approved\") | .rollup'"

echo "5. Final claim, approval and ledger"
call "$PALM" -X POST "$BASE/api/claims/$ID/final"
wait_for "final claim in Northstar's inbox" "call $NST $BASE/api/claims/$ID | jq -r 'select(.awaitingPayer==true and .stage==\"claim\") | .stage'"
call "$NST" -X POST "$BASE/api/claims/$ID/final-decision" -d '{"approve":true}'
wait_for "ledger entry created" "call $PALM $BASE/api/claims/$ID | jq -r '.ledger.amount // empty'"

echo "6. Batch payment and signed processor webhook"
AUTH=$(call "$PALM" "$BASE/api/claims/$ID" | jq -r .authNo)
BATCH=$(call "$NST" -X POST "$BASE/api/payers/HMO-0XB/batches" -d "{\"hospitalCode\":\"$PALM_CODE\",\"authNos\":[\"$AUTH\"]}" | jq -r .batchNo)
call "$NST" -X POST "$BASE/api/batches/$BATCH/simulate-payment"
wait_for "Palmgrove sees the claim paid" "call $PALM $BASE/api/claims/$ID | jq -r 'select(.ledger.status==\"paid\") | .ledger.status'"

echo "7. Riverside to Sapphire, under the automatic limit: Sapphire's system decides"
ID2=$(call "$RIV" -X POST "$BASE/api/hospitals/$RIV_CODE/preauth" -d '{"memberNo":"SAP-30117-01","consent":true,"diagnosisCode":"B54","lines":[{"code":"CONS-01","amount":15000},{"code":"LAB-12","amount":8000}]}' | jq -r .id)
wait_for "decided automatically at tariff" "call $RIV $BASE/api/claims/$ID2 | jq -r 'select(.rollup==\"partial\") | .approvedTotal'"

echo "8. Riverside to Sapphire, over the limit: Sapphire's medical reviewer decides"
ID3=$(call "$RIV" -X POST "$BASE/api/hospitals/$RIV_CODE/preauth" -d '{"memberNo":"SAP-30117-01","consent":true,"diagnosisCode":"R51","lines":[{"code":"IMG-40","amount":180000}]}' | jq -r .id)
wait_for "referred to Sapphire's reviewer inbox" "call $SAP '$BASE/api/claims?awaiting=true' | jq -r '.[] | select(.id==\"$ID3\") | .authNo'"
L3=$(call "$SAP" "$BASE/api/claims/$ID3" | jq -r '.items[0].id')
call "$SAP" -X POST "$BASE/api/claims/$ID3/review" -d "{\"decisions\":[{\"lineId\":\"$L3\",\"decision\":\"approve\"}],\"remark\":\"MRI approved.\"}"
wait_for "Riverside sees the approval" "call $RIV $BASE/api/claims/$ID3 | jq -r 'select(.rollup==\"approved\") | .rollup'"

echo "9. Sapphire offline: the switch retries, then delivers"
call "$OPS" -X POST "$BASE/api/switch/participants/HMO-0XA/online" -d '{"online":false}'
ID4=$(call "$RIV" -X POST "$BASE/api/hospitals/$RIV_CODE/preauth" -d '{"memberNo":"SAP-30117-01","consent":true,"diagnosisCode":"B54","lines":[{"code":"CONS-01","amount":15000}]}' | jq -r .id)
wait_for "message retrying" "call $RIV $BASE/api/claims/$ID4 | jq -r 'select(.delivery==\"retrying\") | .delivery'"
call "$OPS" -X POST "$BASE/api/switch/participants/HMO-0XA/online" -d '{"online":true}'
wait_for "delivered after retry and decided" "call $RIV $BASE/api/claims/$ID4 | jq -r 'select(.rollup==\"approved\") | .rollup'"

echo "10. Regulator view"
call "$REG" "$BASE/api/nhia/summary" | jq '{totalClaims, liveClaims, approvalRate, medianHours}'

echo "11. Demo reset from the switch console"
call "$OPS" -X POST "$BASE/api/switch/reset"
[ "$(code "$PALM" "$BASE/api/claims/$ID")" = "404" ] || fail "reset removes demo claims"
[ "$(call "$PALM" "$BASE/api/claims" | jq length)" -ge 5 ] || fail "sample claims reloaded"
echo "  ok: demo data reset, sessions still valid"
echo "All smoke checks passed."
