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

## Running locally

Start Redis:

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
