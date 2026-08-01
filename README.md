# PlanApi

A REST API over Redis that stores nested JSON documents (a health-insurance "plan") with JSON Schema validation, ETag-based conditional read **and** conditional write, flattened key/value storage, and OAuth 2.0 bearer-token security backed by Google. Every write is published to RabbitMQ and indexed into Elasticsearch as a parent-child document tree.

Course project for BigData Indexing (Northeastern).

## Stack

- ASP.NET Core Web API on .NET 10
- StackExchange.Redis
- System.Text.Json (`JsonNode` for tree manipulation)
- JsonSchema.Net (draft-07 validation)
- JWT bearer auth validating Google-issued ID tokens (RS256)
- RabbitMQ.Client 7.x (async API) — durable queue between the API and the indexer
- Elastic.Clients.Elasticsearch 9.x — must match the 9.x server line, not 8.x
- Redis on `localhost:6379`, RabbitMQ on `localhost:5672`, Elasticsearch on `localhost:9200`

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
- **Search indexing** — every successful write publishes to RabbitMQ; a background consumer indexes the plan into Elasticsearch (see below).

## Search indexing

Redis stays the source of truth. Elasticsearch is a derived store and is never read authoritatively.

```
POST / PATCH  ─▶ Redis write ─▶ publish {op, planId, plan}      ┐
DELETE        ─▶ Redis cascade ─▶ publish {op, planId, docIds}  ├─▶ durable queue
                                                                ┘        │
                                        IndexerService (prefetch=1, manual ack) ─▶ Elasticsearch
```

- **Durable queue, persistent messages, publisher confirms.** All three are needed: a durable queue holding non-persistent messages still empties on a broker restart.
- **Manual ack.** The consumer acks only after Elasticsearch confirms the write, so a crash mid-index leaves the message queued for redelivery.
- **Publish failures do not fail the request.** Redis has already committed; the error is logged and the index catches up on the next write to that plan.
- **PATCH publishes the merged plan**, not the partial body, so an update is the same code path as a create and the index cannot drift.

### Parent-child mapping

Documents are flattened one per `objectId`, each indexed with `routing` = **the root plan's objectId** so the whole tree shares a shard (Elasticsearch resolves joins within a shard, never across them).

| Nested JSON field | ES `plan_join.name` | Parent |
|---|---|---|
| *(root)* | `plan` | — |
| `planCostShares` | `planCostShares` | plan |
| `linkedPlanServices[]` | `linkedPlanServices` | plan |
| `linkedService` | `linkedService` | that `linkedPlanServices` |
| `planserviceCostShares` | `planServiceCostShares` | that `linkedPlanServices` |

Note the last row: the JSON field is lowercase `planserviceCostShares` (from `schema.json`) while the join relation is capital-S `planServiceCostShares` (from the index mapping). A mismatch returns **empty results with no error**.

For deep children, `routing` (root plan id) and `plan_join.parent` (immediate parent id) are deliberately *different values* — routing selects the shard, parent defines the relationship.

### Creating the index

The app does not create the index. Create it once before the first write:

```bash
curl -X PUT http://localhost:9200/demo -H 'Content-Type: application/json' -d '{
  "mappings": { "properties": {
    "copay":      { "type": "integer" },
    "deductible": { "type": "integer" },
    "name":       { "type": "text" },
    "objectId":   { "type": "keyword" },
    "objectType": { "type": "keyword" },
    "plan_join":  { "type": "join", "eager_global_ordinals": true,
      "relations": {
        "plan": ["planCostShares", "linkedPlanServices"],
        "linkedPlanServices": ["linkedService", "planServiceCostShares"]
      } } } }
}'
```

### Configuration

| Key | Default (`appsettings.json`) | In Docker |
|---|---|---|
| `RabbitMq:HostName` | `localhost` | `rabbitmq` |
| `RabbitMq:Queue` | `plan-index` | `plan-index` |
| `Elasticsearch:Uri` | `http://localhost:9200` | `http://elasticsearch:9200` |
| `Elasticsearch:Index` | `demo` | `demo` |

`RabbitMq:Port`, `RabbitMq:UserName`, and `RabbitMq:Password` also exist, defaulting to `5672` / `guest` / `guest`.

### Known limitations

- A message that always fails to index is requeued indefinitely; there is no dead-letter queue.
- A failed publish leaves Elasticsearch stale for that plan until its next write.

## Run from source with Docker

Builds the API image from your local source and runs it with Redis:

```bash
docker compose up --build
```

API at <http://localhost:8080>. Because the endpoints are secured, call them with a bearer token (see [Authentication](#authentication)).

## Run the published image (no clone required)

Save as `compose.yml` in an empty folder:

The published image needs Redis, RabbitMQ, and Elasticsearch alongside it. The full file ships in this repo as `docker-compose.ghcr.yml` — copy it into an empty folder as `compose.yml`, or if you've cloned the repo just run it directly:

```bash
docker compose -f docker-compose.ghcr.yml up   # defaults to image tag 0.3.0
VERSION=0.2.0 docker compose -f docker-compose.ghcr.yml up   # override the tag
```

Then create the `demo` index once (see [Creating the index](#creating-the-index)). Note that tag `0.2.0` and earlier predate search indexing and will ignore the RabbitMQ and Elasticsearch settings.

## Run from source with .NET

For active development against the .NET toolchain. Start the backing services, then the API:

```bash
docker compose up -d redis Elasticsearch Kibana RabbitMQ
dotnet run
```

The defaults in `appsettings.json` point at `localhost`, which matches the published ports, so no extra configuration is needed. Create the `demo` index once (see [Creating the index](#creating-the-index)) before the first write.

In Development the Scalar API explorer is available at <http://localhost:5274/scalar/v1>. Kibana Dev Tools is at <http://localhost:5601>, and the RabbitMQ management UI at <http://localhost:15672> (guest / guest).

On startup you should see `Declared durable queue plan-index` and `Indexer consuming plan-index`. If RabbitMQ is unreachable the API still boots and serves reads; only indexing is affected.

## Testing the secured flow

1. Obtain a Google `id_token` (see [Authentication](#authentication)).
2. `POST /v1/plan` with the sample body in `usecase.json` → **201** + `ETag`.
3. `GET /v1/plan/{objectId}` → **200**; repeat with `If-None-Match: <etag>` → **304**.
4. `PATCH /v1/plan/{objectId}` with `If-Match: <etag>` and a partial body → **200** + new `ETag`; reuse the stale ETag → **412**; omit `If-Match` → **428**.
5. `DELETE /v1/plan/{objectId}` → **204**.

Reset Redis between runs: `docker exec -it planapi-redis redis-cli FLUSHALL`. Note that a second POST of the same body returns **409** until you do.

### Verifying the index

After step 2, `usecase.json` becomes 8 documents:

```bash
curl -s http://localhost:9200/demo/_count                             # 8
docker exec planapi-RabbitMQ-1 rabbitmqctl list_queues name messages  # 0 pending
```

Then in Kibana Dev Tools:

```
# children of the plan                                    -> 3 hits
GET /demo/_search
{ "query": { "has_parent": { "parent_type": "plan", "query": { "match_all": {} } } } }

# grandchildren                                           -> 4 hits
GET /demo/_search
{ "query": { "has_parent": { "parent_type": "linkedPlanServices", "query": { "match_all": {} } } } }

# has_child with a range predicate                        -> 1 hit
GET /demo/_search
{ "query": { "has_child": { "type": "planServiceCostShares",
  "query": { "range": { "copay": { "gte": 1 } } } } } }

# same, higher threshold                                  -> 1 hit
GET /demo/_search
{ "query": { "has_child": { "type": "planServiceCostShares",
  "query": { "range": { "copay": { "gte": 120 } } } } } }
```

`has_child` returns *parents*, so queries 3 and 4 match the `linkedPlanServices` whose cost-share clears the threshold. Query 3 returns 1 rather than 2 because the other service's copay is `0`.

After step 4, a PATCH raising that copay to `130` makes query 3 return **2** — the change propagating through Redis, RabbitMQ, and into the index. After step 5, `_count` returns **0**: the plan and all 7 descendants are gone from both stores.

Fetching a child document directly requires the routing value:

```bash
curl -s "http://localhost:9200/demo/_doc/1234512xvc1314asdfs-503?routing=12xvxc345ssdsds-508"
```

On a single-shard index the `routing` parameter is not strictly required for a GET, since there is only one shard to query. It is always required when *indexing* a child — Elasticsearch rejects the write with `[routing] is missing for join field [plan_join]`.

## Data model

A `plan` decomposes into Redis records:

```
plan
├─ planCostShares            → membercostshare
└─ linkedPlanServices[]      → planservice
    ├─ linkedService         → service
    └─ planserviceCostShares → membercostshare
```

The same tree becomes 8 Elasticsearch documents in the `demo` index, related by the `plan_join` field and co-located by routing — see [Search indexing](#search-indexing).
