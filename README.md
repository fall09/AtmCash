# ATM Cash Management Backend

ASP.NET Core (.NET 10) Web API with EF Core (code-first, migrations) on PostgreSQL. No UI. Implements the ATM lifecycle, cassette configuration and cash loading, withdrawal quote/confirm with minimum-note selection, deposits, an append-only cash ledger, reconciliation with four-eyes approval, an operations report and a low-cash alert outbox.

## 1. How to run

Requirements: Docker Desktop. For running tests locally also the .NET 10 SDK.

### API + database

```bash
docker compose up --build
```

- API: http://localhost:8080 (Swagger UI: http://localhost:8080/swagger, OpenAPI: `/openapi/v1.json`)
- Health check: http://localhost:8080/health (includes a DB check)
- PostgreSQL is exposed on host port **5433** (container 5432).
- Migrations are applied automatically on API start (with retry until the DB is ready).
- Quote lifetime is 60 s as specified. For manual testing in Swagger it is configurable:
  `QUOTE_TTL_SECONDS=600 docker compose up --build`
- Clean start: `docker compose down -v && docker compose up --build`

### Tests

```bash
dotnet test
```

Docker must be running: the integration tests start a throw-away PostgreSQL 17 container via Testcontainers and host the real API in-process (`WebApplicationFactory`), so migrations, locks, constraints and the DB trigger are exercised for real. 35 tests:

- `NoteSelectorTests` – the 8 acceptance tests from B3 plus 6 extra edge cases (pure unit tests).
- `Integration/*` – concurrency (30 parallel quotes against 10 notes; 10 parallel confirms with the same idempotency key), full withdrawal flow, expiry/cancel, deposits, ledger consistency and immutability, reconciliation + four-eyes, low-cash alert and operations report.

### Postman

Import both files from `postman-collection/`:

- `AtmCash.postman_collection.json`
- `AtmCash.local.postman_environment.json` (`baseUrl = http://localhost:8080`)

Select the environment and run the collection with the Collection Runner (with `docker compose up` running). The **Main scenario** folder is the end-to-end flow: create ATM → configure cassettes → load cash → put in service → quote → confirm (Idempotency-Key) → replay with the same key → confirm with a new key (409) → deposit → maintenance → reconcile (small difference) → reconcile (large difference, approval needed) → same-user approval rejected → approval by a second user → back in service → ledger / consistency / report / alerts. IDs are chained through collection variables and every request has assertions. The **Extra** folder covers alternative quotes (LOWER/UPPER) and validation errors.

## 2. API overview

| Area | Endpoint |
|---|---|
| ATM | `POST /api/atms`, `GET /api/atms`, `GET /api/atms/{id}` (inventory per cassette, per denomination, per currency) |
| Setup | `PUT /api/atms/{id}/cassettes`, `POST /api/atms/{id}/load`, `POST /api/atms/{id}/unload`, `POST /api/atms/{id}/transitions` |
| Withdrawal | `POST /api/atms/{id}/withdrawals/quotes`, `GET /api/quotes/{id}`, `POST /api/quotes/{id}/confirm` (header `Idempotency-Key`), `POST /api/quotes/{id}/cancel` |
| Deposit | `POST /api/atms/{id}/deposits` |
| Ledger | `GET /api/atms/{id}/movements`, `GET /api/atms/{id}/ledger/consistency` |
| Reconciliation | `POST /api/atms/{id}/reconciliations`, `GET /api/atms/{id}/reconciliations`, `GET /api/reconciliations/{id}`, `POST /api/reconciliations/{id}/approve` (header `X-User`), `GET /api/exchange-rates` |
| Reporting | `GET /api/reports/operations?atmId=`, `GET /api/alerts?atmId=` |

Errors are RFC 7807 `application/problem+json` with a stable machine-readable `code` (e.g. `DAILY_LIMIT_EXCEEDED`, `NO_PAYABLE_AMOUNT`, `QUOTE_ALREADY_CONFIRMED`, `IDEMPOTENCY_KEY_REUSED`, `RECONCILIATION_PENDING_APPROVAL`, `SAME_USER_APPROVAL`, `INVALID_ATM_STATE`) and a `traceId`. Business rule violations are 409/422, validation errors 400, unexpected errors 500 with a generic message (no internals leaked). Enums are serialized as `UPPER_SNAKE_CASE`.

## 3. Architecture and key design decisions

Layout: `Domain` (entities, rules, the note-selection algorithm; no EF/HTTP dependencies in the algorithm), `Application` (services, DTOs, background workers), `Infrastructure` (DbContext, mappings, migrations, locking, exception handler), `Controllers` (thin).

### 3.1 Note selection (B3)
Bounded knapsack by dynamic programming over `amount / 10` (all denominations are multiples of 10; unit = 10). State = minimum number of notes to reach each amount using at most the available count of each denomination. This gives the *minimum number of notes* exactly, which a greedy algorithm does not guarantee when counts are limited (e.g. missing 50s). Complexity is O(denominations × amount/10 × counts) – trivial for ATM sizes.

- If the exact amount is payable → a single `EXACT` option.
- Otherwise the nearest lower and nearest higher payable amounts are computed from the same table (`LOWER`, `UPPER`). The upper alternative is capped by the card's remaining daily limit.
- Cassettes holding the same denomination are merged for the DP, then the notes are taken from the cassette with the **higher fill ratio** (count/capacity) first so cassettes stay balanced.

### 3.2 Quote reservation and expiry
A quote reserves the notes it offered for 60 s so that a second customer cannot be promised the same notes. With alternatives, the reservation per cassette is the **maximum across the options** (so whichever option is chosen is guaranteed). Confirm debits only the chosen option and releases the entire reservation. Expiry is enforced lazily (any confirm/cancel of an expired quote releases it and returns 409 `QUOTE_EXPIRED`) and by a background sweeper (`QuoteExpiryService`, every 5 s) so abandoned quotes free their notes.

### 3.3 Concurrency
- Every mutating operation on an ATM runs in a transaction that first takes a row lock on that ATM (`SELECT … FOR UPDATE`). All operations on one ATM are therefore serialized; different ATMs run in parallel. This is simple and obviously correct for a dispenser, where the contended resource is physically one machine.
- The daily card limit is checked per card + currency under a Postgres transaction-level advisory lock (`pg_advisory_xact_lock`), so the same card used on **two different ATMs** at the same time cannot exceed the limit.
- Defence in depth in the database: `CHECK` constraints on cassettes (`0 ≤ ReservedCount ≤ Count ≤ Capacity`) – a bug can never produce a negative or overfull cassette.
- Proven by the integration tests (parallel quotes can never reserve more notes than exist; parallel confirms dispense once).

### 3.4 Idempotency
`POST …/confirm` requires an `Idempotency-Key` header. The key and the request fingerprint are stored in `IdempotencyRecords` in the **same transaction** as the debit. Same key + same request → the original response is returned with `Idempotent-Replayed: true` and nothing is debited again. Same key with a different request → 409 `IDEMPOTENCY_KEY_REUSED`. A new key for an already confirmed quote → 409 `QUOTE_ALREADY_CONFIRMED`.

### 3.5 Ledger (B8)
Every change of cassette contents is written as a `CassetteMovement` (Load, Unload, Withdrawal, Deposit, Reservation, ReservationRelease, ReconciliationAdjustment) in the same transaction as the state change. The table is **immutable**: a database trigger rejects `UPDATE` and `DELETE` (migration `LedgerImmutability`, tested). `GET …/ledger/consistency` re-computes each cassette from the ledger (sum of deltas) and compares it with the stored counts, in a RepeatableRead snapshot so concurrent traffic cannot produce false alarms.

### 3.6 Deposits (B7)
Accepted notes go to the matching recycling cassette (emptiest first); notes without a matching cassette (e.g. EUR when no EUR cassette exists) and overflow go to the deposit cassette; if that is full they are returned. Unrecognised notes are never accepted and are returned. The response states for every note where it went and why. Deposit-cassette contents are tracked per (currency, denomination), count in physical totals, but are **not dispensable**.

### 3.7 Reconciliation (B9)
In maintenance, the operator enters the physically counted notes for each recycling cassette (and optionally the full deposit-cassette count). Differences are applied immediately as `ReconciliationAdjustment` ledger entries. The value of the differences is converted to TRY with the seeded rate table and summed as absolute values; above 1,000 TRY the reconciliation becomes `AWAITING_APPROVAL` and the ATM **cannot return to service** until a *different* user approves it (four-eyes; `X-User` header, compared case-insensitively).

### 3.8 Reporting and low-cash alerts (B10)
`GET /api/reports/operations` aggregates per ATM / network: status, per-cassette count, reserved and available notes, per-currency totals. When the available notes of a cassette fall from ≥10 % to <10 % of capacity (edge-triggered, so no alert storm), an `OUTBOX` row is written **in the same transaction** as the operation that caused it (transactional outbox). `OutboxPublisherService` polls every 5 s with `FOR UPDATE SKIP LOCKED` (safe with several instances), publishes (currently: structured log at warning level) and marks `SentAt`. Replacing the log call with a real broker/e-mail publisher is a one-class change.

### 3.9 Other decisions
- **Card numbers** are never stored: only an HMAC-SHA256 with a server-side key (`Security:CardHashKey`, overridable by environment variable) is kept for the daily-limit lookup.
- `ProblemDetails` are produced by a global exception handler that writes JSON directly (independent of the client's `Accept` header).
- Query splitting is enabled for EF Core to avoid cartesian explosions when loading ATM + cassettes + holdings.

## 4. Assumptions

- Denominations: 20, 50, 100, 500; currencies: TRY, USD, EUR. An ATM has 5 cassettes: slot 1 = deposit cassette, slots 2–5 = recycling cassettes (all four must be configured, 1–2 of them may be foreign currency). Cassette capacity: 100 notes.
- Daily withdrawal limit per card: 20,000 TRY / 1,000 USD / 1,000 EUR, per calendar day in Turkey (fixed UTC+3 offset instead of a tz database). Pending (reserved) quotes count against the limit using the largest option's amount. A request above the remaining limit is rejected (`DAILY_LIMIT_EXCEEDED`); only the upper *alternative* is capped.
- Card number: 12–19 digits; no Luhn / PIN / authentication (out of scope).
- Manual state transitions: `LOADED → IN_SERVICE`, `IN_SERVICE ⇄ MAINTENANCE`. `UNCONFIGURED → CONFIGURED → LOADED` happen as a side effect of configuring and loading. Cash can only be unloaded in `MAINTENANCE`. Leaving maintenance is refused while quotes hold reservations or a reconciliation awaits approval.
- Operators are identified only by the `X-User` header (no authentication/authorization – out of scope).
- Reconciliation value = Σ |difference × denomination × TRY rate| with seeded rates TRY 1, USD 42, EUR 49.
- The deposit cassette cannot be loaded manually, and its notes are never dispensed.
- The low-cash threshold is 10 % of cassette capacity based on *available* (not reserved) notes.

## 5. Not done / limitations

- No authentication or authorization; `X-User` is trusted.
- The outbox publisher only logs; there is no real broker/e-mail/webhook integration or retry/back-off policy.
- No pagination on list endpoints (movements have a `take` limit).
- Daily-limit day boundary uses a fixed UTC+3 offset; it would need a proper time-zone setting for ATMs in other countries.
- Exchange rates are seeded data without an admin endpoint to change them.
- Row-level ATM locking serializes all operations on one ATM; fine for one physical machine, would need rethinking for much higher per-ATM throughput.
- The reservation model keeps notes reserved for the maximum across options, which can briefly make a few notes unavailable to others for up to the quote TTL.
- Test coverage is focused on business rules and concurrency; there are no load tests.

## 6. AI tool usage

**Tool:** Claude (Anthropic) via Claude Code.

**Used for:** code generation, debugging and drafting this README. I ran every step myself (build, `dotnet test`, `docker compose up`, Swagger, Postman) and fixed issues based on the real output.

**Issues found and fixed while testing:** duplicate member name (compile error); validation attribute placement causing a 500; missing `code` in error responses for some `Accept` headers; EF Core multiple-include warning (split queries); configurable quote TTL for manual testing; a wrong Postman assertion.

**Verification:** 35/35 tests pass, the Postman scenario is green, and a fresh clone starts with `docker compose up --build` from an empty database.

**What had to be corrected after running it:**

- Compile error from a duplicated `Name` member on the `Atm` entity – renamed.
- `[property: Required]` on record parameters caused a 500 (`InvalidOperationException` in validation metadata) – attributes moved to the parameter target.
- Error responses lacked the `code` field when Swagger sent `Accept: text/plain`, because the built-in ProblemDetails service could not negotiate – replaced by a handler that writes `application/problem+json` directly.
- EF Core warned about multiple collection includes – enabled split queries.
- A 409 on confirm during manual testing was only the 60 s quote expiry – made the TTL configurable via an environment variable (default still 60 s).
- A Postman assertion expected `null` for a missing header, Postman returns `undefined` – fixed with `headers.has(...)`.
- The Postman app converted the first collection folder to its own format and deleted the JSON files – moved the export to `postman-collection/`.

**Verification:** 35/35 tests pass locally, the full Postman scenario is green, and a fresh clone of this repository starts with `docker compose up --build` from an empty database.
