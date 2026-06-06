# PlanApi

A REST API over Redis that stores a nested JSON document (a health-insurance "plan") with strict JSON Schema validation, ETag-based conditional reads, and flattened key/value storage.

Course project for BigData Indexing (Northeastern), Demo 1.

## Stack

- ASP.NET Core Web API on .NET 10
- StackExchange.Redis
- System.Text.Json (`JsonNode` for tree manipulation)
- JsonSchema.Net (draft-07 validation)
- Redis on `localhost:6379`

## Endpoints

| Method | Route                  | Success                                   | Failure |
|--------|------------------------|-------------------------------------------|---------|
| POST   | `/v1/plan`             | 201 Created + `ETag` + `Location` header  | 400 (schema errors), 409 (objectId exists) |
| GET    | `/v1/plan/{objectId}`  | 200 + body + `ETag`; 304 on `If-None-Match` hit | 404 |
| DELETE | `/v1/plan/{objectId}`  | 204 No Content                            | 404 |

## Key behaviors

- **Schema validation** on POST returns 400 with a readable error list when payload doesn't match the plan schema.
- **ETag** is `SHA-256` of the canonical (sorted-key) reassembled JSON; returned on POST and GET; `If-None-Match` returns 304.
- **Flattened storage**: the document is decomposed into one Redis record per nested object (key `{objectType}:{objectId}`); parents hold reference strings to children. Reassembled on GET.

## Quick start (no clone required)

Run the published version without installing .NET or cloning the repo. Save the following as `compose.yml` in any empty folder:

```yaml
services:
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
  api:
    image: ghcr.io/sri-ln/planapi:${VERSION:-0.1.0}
    ports: ["8080:8080"]
    environment:
      ConnectionStrings__Redis: "redis:6379"
    depends_on: [redis]
```

Then:

```bash
docker compose up
```

API at <http://localhost:8080>. Defaults to image tag `0.1.0`. Override with `VERSION=0.2.0 docker compose up`.

If you've already cloned this repo, the same file ships as `docker-compose.ghcr.yml`:

```bash
docker compose -f docker-compose.ghcr.yml up
```

## Run from source with Docker

Builds the API image from your local source tree and runs it alongside Redis:

```bash
docker compose up --build
```

API at <http://localhost:8080>.

## Run from source with .NET

For active development against the .NET toolchain. Start Redis:

```bash
docker run --rm -p 6379:6379 redis
```

Run the API:

```bash
dotnet run
```

## Data model

A `plan` decomposes into 8 Redis records:

```
plan
├─ planCostShares        → membercostshare
├─ planservice
│   ├─ linkedService     → service
│   └─ planserviceCostShares → membercostshare
└─ planservice
    ├─ linkedService     → service
    └─ planserviceCostShares → membercostshare
```
