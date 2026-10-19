# HealthSwitch MVP

A working MVP of a neutral health claims switch for Nigeria. Hospitals send eligibility checks, pre-authorisations and claims once; the switch queues and routes them to the right payer (HMO, state scheme), carries decisions and documents back, keeps a shared ledger, settles batch payments and gives NHIA an aggregated view.

All organisations, people and amounts are fictional sample data.

## Six separate websites

Every organisation has its own website, look and login, all served by the one app. The home page (`/`) lists them with their demo logins.

| Website | Path | Organisation | Demo login |
| --- | --- | --- | --- |
| Hospital | `/palmgrove` | Palmgrove Hospital, Ikeja | `billing@palmgrove.ng` / `palmgrove2026` |
| Hospital | `/riverside` | Riverside Medical, Port Harcourt | `billing@riversidemedical.ng` / `riverside2026` |
| HMO | `/northstar` | Northstar HMO: no system of its own, reviews every request in the HealthSwitch portal | `claims@northstarhmo.ng` / `northstar2026` |
| HMO | `/sapphire` | Sapphire HMO: its own system, connected by API. Its rules decide requests under ₦50,000; larger ones go to its medical reviewer | `claims@sapphirehmo.ng` / `sapphire2026` |
| Switch | `/switch` | HealthSwitch operations: message flow, retries, dead letters, participant on/off, demo reset | `ops@healthswitch.ng` / `switch2026` |
| Regulator | `/nhia` | Read-only aggregates, no patient-level data (a demo, not an NHIA system) | `analyst@regulator.demo` / `regulator2026` |

Each sign-in returns a signed token (HMAC-SHA256) that names the organisation and role. Every `/api` call checks it: a hospital only sees and changes its own claims, an HMO only the requests sent to it, and only the switch operator can use the console. The tokens are kept per website in the browser, so all six can stay signed in side by side in one browser.

## What is real in this MVP

| Part | Status |
| --- | --- |
| HCX-style gateway (`/v1/{resource}/{action}`, `x-hcx-*` headers, `202 Accepted`, idempotent `api_call_id`, error codes) | Real |
| Message queues: `q.route`, `q.deliver.api`, `q.deliver.portal`, retries, `q.dead` (MassTransit on RabbitMQ) | Real |
| PostgreSQL storage: claims, line items, remarks, documents, ledger, batches, message log | Real |
| Separate hospital, HMO, switch and regulator websites with their own logins and data scoping | Real (demo logins, signed session tokens) |
| Hospital portal, payer review with remarks and document requests, file upload, final claim | Real |
| Batch payment with a signed (HMAC-SHA256) processor webhook | Real code; the processor is a sandbox stand-in |
| API-connected payer (Sapphire HMO) | Its system is simulated inside the app at `/sim/...` and called over HTTP like a real payer. Its rules engine and reviewer website share this app's database in the MVP |
| NHIA dashboard | Real aggregation from the database, plus 180 sample claims for a month of history |
| FHIR R4 payloads shaped on NHCX ClaimBundle | Simplified, not profile-validated |
| Security | Demo logins per organisation plus an optional shared demo key (`DEMO_ACCESS_KEY`). Real user management, OAuth2 for systems and payload encryption (JWE) come in the pilot |

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

Open http://localhost:8080 for the list of websites. RabbitMQ's console is at http://localhost:15672 (guest / guest), where you can watch the `q.*` queues.

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

Portal (bearer token from `POST /api/login`): `/api/reference`, `/api/hospitals/{code}/eligibility`, `/api/hospitals/{code}/preauth`, `/api/claims`, `/api/claims/{id}/review`, `/api/claims/{id}/documents` (multipart), `/api/claims/{id}/final`, `/api/claims/{id}/final-decision`, `/api/payers/{code}/batches`, `/api/batches/{no}/simulate-payment`, `/api/switch/overview`, `/api/switch/reset`, `/api/nhia/summary`.

## Sample data and reset

The sample data has a version (`Seeder.SeedVersion`). When the app starts against a database with an older version, it drops its own tables and loads fresh sample data, so a schema change never needs manual database work. **Reset demo data** in the switch console does the same at any time, for a clean start before a meeting.

## Next steps toward the pilot

* Real user accounts per organisation and role (several users, password policy, MFA) and OAuth2 client credentials for connected systems.
* Payload encryption (JWE) for API-connected payers, as in the HCX protocol.
* EF Core migrations instead of `EnsureCreated`; file storage in object storage instead of the database.
* Longer retry windows (hours), split consumers into separate services, OpenTelemetry tracing.
* A real payment processor sandbox (Monnify, Paystack or Flutterwave) behind the existing webhook.
