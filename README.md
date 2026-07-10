# PlanApi

A REST API over Redis that stores nested JSON documents (a health-insurance "plan") with JSON Schema validation, ETag-based conditional read **and** conditional write, flattened key/value storage, and OAuth 2.0 bearer-token security backed by Google.

Course project for BigData Indexing (Northeastern).

## Stack

- ASP.NET Core Web API on .NET 10
- StackExchange.Redis
- System.Text.Json (`JsonNode` for tree manipulation)
- JsonSchema.Net (draft-07 validation)
- JWT bearer auth validating Google-issued ID tokens (RS256)
- Redis on `localhost:6379`

## Authentication

All `/v1/plan` endpoints require a bearer token:

```
Authorization: Bearer <google_id_token>
```

- The API is a **resource server**: it validates Google-issued **ID tokens** (signed RS256) against Google's published JWKS keys. It never issues tokens.
- Signature, issuer (`https://accounts.google.com`), audience (`Google:ClientId`), and expiry are all validated. Missing / invalid / expired → **401**.
- Configure your Google OAuth **client ID** in `appsettings.json` under `Google:ClientId`.
- To get a token for testing, use the [Google OAuth 2.0 Playground](https://developers.google.com/oauthplayground/) (or Postman's OAuth 2.0 helper) with your own client credentials, and use the **`id_token`** — not the access token (Google access tokens are opaque and won't validate).

## Endpoints

| Method | Route                 | Success                                             | Failure |
|--------|-----------------------|-----------------------------------------------------|---------|
| POST   | `/v1/plan`            | 201 Created + `ETag` + `Location`                   | 400 (schema), 409 (exists), 401 |
| GET    | `/v1/plan/{objectId}` | 200 + body + `ETag`; 304 on `If-None-Match` hit     | 404, 401 |
| PATCH  | `/v1/plan/{objectId}` | 200 + merged body + new `ETag`                      | 400 (schema), 412 (stale `If-Match`), 428 (missing `If-Match`), 404, 401 |
| DELETE | `/v1/plan/{objectId}` | 204 No Content                                      | 404, 401 |

## Key behaviors

- **Schema validation** — POST, and the *merged result* of a PATCH, are validated against the plan schema; failures return 400 with a readable error list.
- **Conditional read** — `ETag` is `SHA-256` of the canonical (sorted-key) reassembled JSON; `If-None-Match` returns 304 when unchanged.
- **Conditional write** — PATCH requires `If-Match`: missing → **428**, stale ETag → **412**. This enforces "update if not changed" and prevents lost updates.
- **Merge (PATCH) semantics** — objects are deep-merged; a `null` value deletes a member (RFC 7386); the `linkedPlanServices` array is **upserted by `objectId`** (matched items deep-merged, new `objectId`s appended).
- **Flattened storage** — the document is decomposed into one Redis record per nested object (key `{objectType}:{objectId}`); parents hold reference strings to children, reassembled on GET.

## Run from source with Docker

Builds the API image from your local source and runs it with Redis:

```bash
docker compose up --build
```

API at <http://localhost:8080>. Because the endpoints are secured, call them with a bearer token (see [Authentication](#authentication)).

## Run the published image (no clone required)

Save as `compose.yml` in an empty folder:

```yaml
services:
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
  api:
    image: ghcr.io/sri-ln/planapi:${VERSION:-0.2.0}
    ports: ["8080:8080"]
    environment:
      ConnectionStrings__Redis: "redis:6379"
    depends_on: [redis]
```

```bash
docker compose up            # defaults to image tag 0.2.0; override with VERSION=x.y.z
```

If you've cloned the repo, the same file ships as `docker-compose.ghcr.yml`:

```bash
docker compose -f docker-compose.ghcr.yml up
```

## Run from source with .NET

For active development against the .NET toolchain. Start Redis, then the API:

```bash
docker compose up -d redis
dotnet run
```

In Development the Scalar API explorer is available at <http://localhost:5274/scalar/v1>.

## Testing the secured flow

1. Obtain a Google `id_token` (see [Authentication](#authentication)).
2. `POST /v1/plan` with the sample body in `usecase.json` → **201** + `ETag`.
3. `GET /v1/plan/{objectId}` → **200**; repeat with `If-None-Match: <etag>` → **304**.
4. `PATCH /v1/plan/{objectId}` with `If-Match: <etag>` and a partial body → **200** + new `ETag`; reuse the stale ETag → **412**; omit `If-Match` → **428**.
5. `DELETE /v1/plan/{objectId}` → **204**.

Reset Redis between runs: `docker exec -it planapi-redis redis-cli FLUSHALL`.

## Data model

A `plan` decomposes into Redis records:

```
plan
├─ planCostShares            → membercostshare
└─ linkedPlanServices[]      → planservice
    ├─ linkedService         → service
    └─ planserviceCostShares → membercostshare
```
