# Driving Licence Onboarding Workflow

A .NET backend and Blazor/Radzen interface that accepts driving-licence applications, persists them in PostgreSQL, and processes them asynchronously through RabbitMQ. It includes a deterministic simulated photo check, configurable risk rules, manual review, a durable audit trail, retries, idempotent consumption, Aspire orchestration, and OpenTelemetry.

## Run with Aspire (recommended)

Prerequisites: .NET 8 SDK or newer, Docker Desktop, and HTTPS development certificates (`dotnet dev-certs https --trust`). All projects target .NET 8.

```bash
dotnet run --project src/DrivingLicence.AppHost
```

The command opens or prints the Aspire dashboard URL. The dashboard shows all five resources, structured logs, distributed traces, metrics, health, endpoints, PostgreSQL, and RabbitMQ. Click the `web` endpoint to open the Radzen interface; click `api` and append `/swagger` for Swagger. Open the management endpoint on the `messaging` resource to inspect RabbitMQ; this local sample uses `guest` / `guest` for both Aspire and Docker Compose.

Aspire creates and injects PostgreSQL/RabbitMQ connection details and the API service address. No local connection strings need to be edited.

## Run with Docker Compose

Prerequisite: Docker with Compose. No local HTTPS certificate setup is required. Production TLS would normally terminate at a reverse proxy, ingress, or load balancer; the Aspire development flow above continues to provide HTTPS endpoints.

```bash
docker compose up --build
```

Then open:

- Swagger UI: http://localhost:8080/swagger
- Blazor/Radzen UI: http://localhost:8081
- API health: http://localhost:8080/health
- RabbitMQ management: http://localhost:15672 (`guest` / `guest`)

Submit an application (the example deliberately uses an applicant under 21, so a valid photo score leads to manual review):

```bash
curl -X POST http://localhost:8080/api/applications \
  -H "Idempotency-Key: demo-request-001" \
  -F "firstName=John" \
  -F "lastName=Doe" \
  -F "dateOfBirth=2006-05-20" \
  -F "nationalId=AB123456" \
  -F "email=john.doe@example.com" \
  -F "phoneNumber=+306900000000" \
  -F "address=Example Street 10" \
  -F "licenceCategory=B" \
  -F "photo=@face.jpg;type=image/jpeg"
```

Use the returned ID with:

```text
GET  /api/applications/{id}
GET  /api/applications/{id}/history
POST /api/applications/{id}/approve
POST /api/applications/{id}/reject   body: {"reason":"Photo does not meet quality requirements"}
```

Manual review endpoints require `X-Reviewer-Key: dev-reviewer-key` in this local sample. The Blazor UI supplies it automatically. Override `Reviewer__ApiKey` in each service instead of using the sample value outside local development. RabbitMQ's `guest` / `guest` credentials are also development-only and must be replaced through secure configuration outside this exercise.

Stop with `docker compose down`. Add `-v` only when you intentionally want to delete database and photo volumes.

## Architecture

```text
Client -> API -> PostgreSQL (application + audit + outbox in one commit)
                  |
             outbox dispatcher -> RabbitMQ -> Worker
                                               |
                                      state machine + PostgreSQL
```

- **Domain** contains the aggregate, allowed transition map, business checks, and queue contract. Transition methods are the only way to change status and always append history.
- **Infrastructure** contains the EF Core model and reliability tables.
- **API** handles multipart submission, queries, and manual decisions. Photos are stored outside the database in a named volume; only metadata and an opaque path are persisted.
- **Worker** consumes `ApplicationSubmitted` and performs validation, photo checking, risk assessment, and automatic/manual-review decisions.
- **Web** is an interactive-server Blazor application using Radzen components. It submits multipart forms, tracks status/history, and exposes manual review actions.
- **AppHost** declares and starts the complete dependency graph. **ServiceDefaults** instruments API, worker, and web with OpenTelemetry traces, metrics, and logs, service discovery, resilience, and health endpoints.

A custom state machine was chosen over a workflow framework because this flow is small and linear. The allowed-transition map is explicit, has no framework-specific semantics, and is easy to unit test. A richer product with timers, parallel branches, compensation, or business-authored workflows might justify MassTransit Saga, Temporal, or Elsa.

## Workflow and rules

The supported paths are:

```text
Submitted -> ValidatingData -> CheckingPhoto -> RiskAssessment -> Approved
                                                        `-----> PendingManualReview -> Approved | Rejected
                         `-----> Failed
              `-----> Failed
```

Validation rejects missing identity data, malformed email, and applicants under 18. Photo validation permits JPEG/PNG, requires at least 100 bytes, and uses a deterministic score derived from the application ID (30–100), making behavior reproducible without pretending to perform biometrics. Risk sends applicants to review when they are under 21, request a non-B category, appear on the configured watchlist, or score below 70.

Settings are under `Workflow` in the worker configuration and can be overridden with normal .NET environment-variable syntax, for example `Workflow__ManualReviewPhotoQuality=80`.

## Reliability decisions

- **Save succeeds, publish fails:** application, initial audit entry, and outbox row are committed atomically. The API dispatcher continuously retries unpublished rows. Publishing is at-least-once.
- **Duplicate delivery:** each message has a stable outbox/message ID. The worker records it in `processed_messages` in the same `SaveChanges` call as workflow updates. The primary key prevents duplicate records; already-processed messages are ignored. The status guard is a second line of defence.
- **Duplicate submission:** clients may send `Idempotency-Key` (up to 100 characters). Repeating the key returns the existing application instead of creating another one.
- **Worker crash:** before commit, all state changes roll back and RabbitMQ redelivers. After commit but before acknowledgement, idempotency turns redelivery into a no-op.
- **Transient errors:** MassTransit retries three times with exponential backoff. Exhausted messages go to the generated RabbitMQ `_error` queue, which acts as the dead-letter/error queue for inspection and replay.
- **Concurrent updates:** PostgreSQL's `xmin` is configured as an EF concurrency token. Competing decisions cannot silently overwrite one another.
- **Failures:** expected business failures transition to `Failed` with a reason in audit history. Unexpected infrastructure failures are logged as structured JSON and retried/dead-lettered rather than incorrectly changing business state.
- **Invalid transitions:** the domain transition map rejects them; manual endpoints additionally return a clear HTTP 409 problem response.

## Photo-storage choice

Multipart upload avoids the roughly 33% overhead of base64 and is natural for binary content. The sample writes to a Docker volume through a configurable path. Before production, this would be replaced by object storage using an injected interface, with content sniffing, malware scanning, encryption, retention controls, signed access, and cleanup of a file if the database transaction fails.

## Database migrations

The services apply checked-in EF Core migrations at startup with retry while PostgreSQL becomes available. To create a future migration:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add NameOfChange --project src/DrivingLicence.Infrastructure --startup-project src/DrivingLicence.Api
```

At startup, a compatibility step detects databases created by the earlier `EnsureCreated` version, adds the idempotency column/index, and records the baseline migration while preserving existing applications. A fresh volume is therefore not required.

## Tests

```bash
dotnet test DrivingLicenceOnboarding.sln
```

Seven unit tests cover illegal transitions, audit creation, age validation, each major manual-review rule, and photo-quality rejection. Eleven API/persistence integration tests use PostgreSQL Testcontainers to cover migration/persistence, non-destructive legacy-database adoption, multipart submission, submission idempotency, validation, lookup, reviewer authentication, invalid transitions, manual approval, rejection-reason validation, and decision audit history. One end-to-end messaging test uses real PostgreSQL and RabbitMQ containers to prove that the API outbox publishes a stable message, the Worker consumes it, the workflow reaches approval, and the processed-message marker is committed. Docker must be running for the complete nineteen-test suite.

## Intentional shortcuts and production next steps

This is scoped to a 6–8 hour exercise. Checked-in EF migrations keep `docker compose up` self-contained; production should run reviewed migrations from a single deployment job rather than application startup. Other next steps:

- replace the local reviewer API key with identity-provider authentication, reviewer roles, and reviewer identity in the audit record;
- request-rate limits;
- a leased/claimed outbox dispatcher for multiple API replicas and retention of published rows;
- extend Testcontainers coverage to RabbitMQ, outbox recovery, duplicate delivery, and API contract tests;
- object storage, file signature validation, antivirus scanning, and orphan cleanup;
- deeper RabbitMQ readiness checks, alerting, and error-queue replay tooling;
- explicit PII encryption/redaction, secrets management, retention/deletion policy, and privacy/security review;
- multiple short-lived workflow messages if individual external checks become slow, rather than one consumer transaction.

## Assumptions and AI usage

- A failed validation/photo check is represented by `Failed`; `Rejected` is reserved for risk/manual business decisions.
- Automatic risk rejection was not invented because the assignment only defines manual-review conditions and otherwise permits approval.
- Photo quality simulation is deterministic and not biometric verification.
- AI assistance was used to scaffold and draft implementation/documentation. The architecture, domain rules, reliability model, tests, and resulting code were reviewed and validated with a clean build/test run. Generated code should be discussed openly during review.
