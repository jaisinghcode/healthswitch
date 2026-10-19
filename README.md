# HealthSwitch MVP

A working MVP of a neutral health claims switch for Nigeria. Hospitals send eligibility checks, pre-authorisations and claims once; the switch queues and routes them to the right payer (HMO, state scheme), carries decisions and documents back, keeps a shared ledger, settles batch payments and gives NHIA an aggregated view.

All organisations, people and amounts are fictional sample data.

## What is real in this MVP

| Part | Status |
| --- | --- |
| HCX-style gateway (`/v1/{resource}/{action}`, `x-hcx-*` headers, `202 Accepted`, idempotent `api_call_id`, error codes) | Real |
| Message queues: `q.route`, `q.deliver.api`, `q.deliver.portal`, retries, `q.dead` (MassTransit on RabbitMQ) | Real |
| PostgreSQL storage: claims, line items, remarks, documents, ledger, batches, message log | Real |
| Hospital portal, payer review with remarks and document requests, file upload, final claim | Real |
| Batch payment with a signed (HMAC-SHA256) processor webhook | Real code; the processor is a sandbox stand-in |
| API-connected payer (Sapphire HMO) | Simulated inside the app at `/sim/...`, called over HTTP like a real payer |
| NHIA dashboard | Real aggregation from the database, plus 180 sample claims for a month of history |
| FHIR R4 payloads shaped on NHCX ClaimBundle | Simplified, not profile-validated |
| Security | One shared demo key (`DEMO_ACCESS_KEY`). Real logins, OAuth2 and payload encryption (JWE) come in the pilot |

## Architecture

```
Browser (wwwroot) ──► /api (portal) ──► GatewayService ──► q.route ──► Router ──► q.deliver.api ────► API payer (/sim) ──► on_* callback ──► gateway
                       /v1 (protocol) ─┘   saves envelope,                        └► q.deliver.portal ──► payer / hospital inbox
                                           returns 202                    failed after retries ──► q.dead ──► replay from switch monitor
```

One deployable (`src/HealthSwitch.Api`) hosts the gateway, the queue consumers and the web portal, so it fits a single free web service. The consumers are separate classes on separate queues and can be split into their own services later.

* `Gateway/GatewayService.cs` validates, stores and enqueues every message.
* `Messaging/Consumers.cs` router, API adapter, portal adapter, dead-letter consumer.
* `Messaging/DeliveryEffects.cs` what happens when a message arrives (inbox, decisions, ledger, payment).
* `Endpoints/PortalApi.cs` the portal API; `Endpoints/ProtocolApi.cs` the protocol endpoints and webhook.
* `Domain/ClaimRules.cs` status roll-up rules (scope document section 7.2), unit-tested.

If `RABBITMQ_URL` is not set, the same queues run in memory, which is handy for quick local runs.

## Run locally

Needs Docker.

```bash
docker compose up --build
```

Open http://localhost:8080. RabbitMQ's console is at http://localhost:15672 (guest / guest), where you can watch the `q.*` queues.

End-to-end check through the real queues:

```bash
bash scripts/smoke.sh http://localhost:8080
```

## Host it for free (Render + Neon + CloudAMQP)

Free tiers as of October 2026. They change, so check each site.

1. **Database: Neon** (neon.com). Sign up, create a project (pick an AWS region near Nigeria, for example Frankfurt), and copy the connection string from **Connect**. It looks like `postgresql://user:pass@ep-....neon.tech/neondb?sslmode=require`.
2. **Queues: CloudAMQP** (cloudamqp.com). Sign up, create an instance on the free **Little Lemur** plan, open it and copy the **AMQP URL** (`amqps://...`).
3. **App: Render** (render.com). Sign up with GitHub, choose **New > Blueprint**, pick this repository. Render reads `render.yaml` and asks for:
   * `DATABASE_URL`: the Neon string from step 1
   * `RABBITMQ_URL`: the CloudAMQP URL from step 2
   * `DEMO_ACCESS_KEY`: any word or phrase; people type it once to open the demo
4. Wait for the first deploy (5 to 10 minutes). Open the `https://healthswitch-xxxx.onrender.com` address Render shows. The app creates its tables and sample data on first start.

**Free-tier behaviour to know before a meeting:** Render puts the app to sleep after 15 minutes without visits and takes about a minute to wake it. Open the link a few minutes before presenting. Neon also pauses after 5 idle minutes and wakes on the first query in about a second.

Run the smoke test against the live site: `bash scripts/smoke.sh https://your-app.onrender.com your-demo-key`.

### Alternative: Oracle Cloud Always Free

An always-on option if the sleep is a problem: an Oracle Cloud Always Free Arm VM (currently documented as 2 OCPU / 12 GB). Install Docker, clone this repo and run `docker compose up -d --build`, then open port 8080 (or put Caddy in front for HTTPS). It needs card verification at sign-up, and free Arm capacity is sometimes unavailable in busy regions.

## API overview

Protocol (HCX style), all return `202 Accepted`:

```
POST /v1/coverageeligibility/check     POST /v1/coverageeligibility/on_check
POST /v1/preauth/submit                POST /v1/preauth/on_submit
POST /v1/claim/submit                  POST /v1/claim/on_submit
POST /v1/communication/request         POST /v1/communication/on_request
POST /v1/paymentnotice/request         POST /v1/paymentnotice/on_request
GET  /v1/requests/{correlationId}      POST /v1/webhooks/processor  (HMAC signed)
Headers: x-hcx-sender_code, x-hcx-recipient_code, x-hcx-api_call_id (UUID), x-hcx-correlation_id, x-hcx-workflow_id
```

Portal: `/api/reference`, `/api/hospitals/{code}/eligibility`, `/api/hospitals/{code}/preauth`, `/api/claims`, `/api/claims/{id}/review`, `/api/claims/{id}/documents` (multipart), `/api/claims/{id}/final`, `/api/claims/{id}/final-decision`, `/api/payers/{code}/batches`, `/api/batches/{no}/simulate-payment`, `/api/switch/overview`, `/api/nhia/summary`.

## Next steps toward the pilot

* Real sign-in per organisation and role (OAuth2 client credentials for systems, user logins for the portal).
* Payload encryption (JWE) for API-connected payers, as in the HCX protocol.
* EF Core migrations instead of `EnsureCreated`; file storage in object storage instead of the database.
* Longer retry windows (hours), split consumers into separate services, OpenTelemetry tracing.
* A real payment processor sandbox (Monnify, Paystack or Flutterwave) behind the existing webhook.
