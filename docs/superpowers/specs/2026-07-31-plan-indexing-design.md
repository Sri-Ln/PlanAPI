# Plan indexing: RabbitMQ producer + Elasticsearch consumer

Demo 3. Every successful Redis write publishes a message; a background consumer
applies the change to the Elasticsearch `demo` index as a parent-child document tree.

## Constraints

- Demo 2 logic (CRUD, PATCH-merge, schema validation, ETag, auth) is unchanged except
  for three publish hooks added after the existing Redis writes.
- `schema.json` is untouched. It keeps the lowercase `planserviceCostShares`.
- Redis is the source of truth. Elasticsearch is derived and never read authoritatively.

## Correction to the working assumptions

The Demo 2 Redis layout is not `HSET` keyed `type__objectId` with `SADD` pointers.
`RedisRepository` uses plain `SET` with keys `type:objectId`, and nested children are
replaced inline by ref strings (`"planCostShares": "membercostshare:1234vxc2324sdf-501"`).
`PlanFlattener.CollectKeysAsync` walks those refs, so **cascaded delete in Redis already
works**. Demo 3 adds only the Elasticsearch half of the cascade.

## Components

| File | Responsibility |
|---|---|
| `Messaging/PlanMessage.cs` | Queue contract. A record, serialized as JSON. |
| `Messaging/RabbitPublisher.cs` | `IPlanPublisher` + implementation. Owns the connection and channel. |
| `Indexing/EsFlattener.cs` | Pure function: nested plan → list of ES docs. No I/O. |
| `Indexing/PlanIndexer.cs` | Thin `ElasticsearchClient` wrapper: index and delete. |
| `Indexing/IndexerService.cs` | `BackgroundService`. Consumes the queue, calls the indexer, acks. |

`EsFlattener` is pure so its output can be inspected without a running Elasticsearch.
A routing or join-name error produces empty query results rather than an error, so the
one dangerous piece is deliberately isolated and independently verifiable.

## Message contract

```json
{ "op": "create" | "update" | "delete", "planId": "...", "plan": { ... }, "docIds": [ ... ] }
```

- `POST` → `op: "create"`, `plan` = the validated request body.
- `PATCH` → `op: "update"`, `plan` = the full merged plan the endpoint already computes.
- `DELETE` → `op: "delete"`, `docIds` = every objectId in the tree, read before deletion.

The consumer treats `create` and `update` identically: re-flatten the whole plan and
upsert every document. No diffing, so PATCH cannot drift from Redis. The distinct `op`
values exist for demo visibility in the RabbitMQ management UI.

The producer knows nothing about join names or routing. All Elasticsearch knowledge
lives in `EsFlattener`.

## Flattening and routing

```
planId = plan.objectId

root                     id=planId        routing=planId   join={name:"plan"}                                  (no parent)
planCostShares           id=pcs.objectId  routing=planId   join={name:"planCostShares",        parent:planId}
linkedPlanServices[i]    id=lps.objectId  routing=planId   join={name:"linkedPlanServices",    parent:planId}
  .linkedService         id=ls.objectId   routing=planId   join={name:"linkedService",         parent:lps.objectId}
  .planserviceCostShares id=psc.objectId  routing=planId   join={name:"planServiceCostShares", parent:lps.objectId}
```

Routing is the root plan id on every document without exception — this keeps the whole
tree on one shard, which parent-child joins require. The parent diverges only at depth 2,
where it is the enclosing `linkedPlanServices` objectId, not the plan id. Routing answers
"which shard"; parent answers "who is my direct parent". They are different values.

The JSON field is `planserviceCostShares` (lowercase s, from `schema.json`); the ES join
relation is `planServiceCostShares` (capital S, from the index mapping). The mapping name
wins in the index layer. This was verified against the live mapping.

Each document carries only its **scalar** fields. The nested members (`planCostShares`,
`linkedPlanServices`, `linkedService`, `planserviceCostShares`) are stripped before
indexing, so the root plan document does not carry a nested `copay` that would collide
with the `copay: integer` mapping or duplicate child data into the parent.

## Delivery and ordering

Durable queue, persistent messages, publisher confirms. The consumer uses
`prefetch=1` with `autoAck=false` and a single consumer, so messages are processed
strictly in order — a create cannot race ahead of its own delete.

Documents are indexed with `refresh: wait_for` so a query run immediately after the HTTP
call already reflects the change, rather than waiting out the ~1s near-real-time delay.

## Error handling

- **Publish fails after a successful Redis write** → log and return the normal success
  response. Redis has already committed; failing the request would report an error for a
  write that persisted. Elasticsearch catches up on the next write.
- **Elasticsearch operation fails** → `BasicNackAsync(requeue: true)` after a short delay,
  leaving the message queued for retry.
  *Known limitation:* a genuinely poison message retries forever. A dead-letter queue after
  N attempts is the correct fix; it is out of scope here, and the retry-forever behaviour is
  the safer failure mode for a demo where losing a message is worse than looping.
- **RabbitMQ unavailable at startup** → automatic recovery enabled and lazy connect, so
  the API boots and serves reads regardless.

## Configuration

Follows the existing `ConnectionStrings__Redis` pattern so the app runs both from the IDE
and in Docker.

| Key | appsettings.json | docker-compose env |
|---|---|---|
| `RabbitMq:HostName` | `localhost` | `rabbitmq` |
| `RabbitMq:Queue` | `plan-index` | `plan-index` |
| `Elasticsearch:Uri` | `http://localhost:9200` | `http://elasticsearch:9200` |
| `Elasticsearch:Index` | `demo` | `demo` |

Docker DNS resolves `elasticsearch` and `rabbitmq` case-insensitively, so the capitalized
compose service names work as-is.

## Packages

- `RabbitMQ.Client` 7.2.1 — fully async (`IChannel`, `BasicPublishAsync`). The 6.x sync API
  found in most tutorials does not compile against it.
- `Elastic.Clients.Elasticsearch` 9.4.2 — the 9.x line, matching the 9.4.4 server. Not 8.x.

## Acceptance

All four queries must return real hits against `demo` after indexing `usecase.json`:

1. `has_parent` with `parent_type: plan` — children of the plan. → **3** hits
2. `has_parent` with `parent_type: linkedPlanServices` — grandchildren. → **4** hits
3. `has_child` with `type: planServiceCostShares` and `range: copay >= 1`. → **1** hit
4. The same with `copay >= 120`. → **1** hit

The sample data satisfies these: `planserviceCostShares` copays are 0 and 175. Queries 3 and 4
return the *parent* `linkedPlanServices`, not the cost-share document — `has_child` matches
parents. Query 3 returns 1 rather than 2 because the other service's copay is 0.

### What these queries do and do not prove

`demo` has **one shard**. Every document therefore lands on the same shard no matter what
routing value is used, so all four queries pass under *any* consistent routing. They verify the
join relation names; they do **not** verify routing. The "grandchildren canary" is a weaker
signal than it appears on a single-shard index.

Two things were verified separately:

- Routing is mandatory at index time. Indexing a child document without routing is rejected with
  `[routing] is missing for join field [plan_join]` (HTTP 400), so a missing value cannot pass
  silently. Only a *wrong* value can.
- Routing correctness was proven by indexing the same tree into two 3-shard indices with the same
  mapping — once with `routing = root plan id` (what `EsFlattener` produces) and once with the
  plausible mistake `routing = each document's own id`:

  | Query | root-plan routing | own-id routing |
  |---|---|---|
  | 1. children of `plan` | 3 | 1 |
  | 2. grandchildren | **4** | **0** |
  | 3. `has_child` copay >= 1 | 1 | 0 |
  | 4. `has_child` copay >= 120 | 1 | 0 |

  Both bulk writes reported `errors=false`. The wrong routing produced zero hits with no error,
  warning, or failed shard — confirming that a routing bug is silent, and that this
  implementation is on the correct side of it.

Reproduce that comparison to answer "how do you know routing is right?" — the single-shard
`demo` index cannot answer it.

## Build order

Each step stops for manual verification before the next begins.

1. Publisher and the POST hook — confirm messages appear in the management UI.
2. `EsFlattener` — print its output for `usecase.json` and check all 8 documents' routing
   and parent values before Elasticsearch is involved.
3. Consumer and indexer — POST, then run all four acceptance queries.
4. PATCH hook — change a copay and watch it reach the index.
5. DELETE hook — cascade removal from both stores.

## Outcome

All five steps are implemented and verified end to end. Notes from the build:

- The DELETE hook must read the tree **before** `repo.DeleteAsync`, because the descendant ids
  exist only inside the tree that call destroys.
- `RabbitPublisher` is registered as a concrete singleton and resolved through for both
  `IPlanPublisher` and `IHostedService`. Registering it twice by type would construct two
  instances holding two connections.
- The queue is declared at startup rather than on first publish, so an empty queue in the
  management UI means "nothing published yet" and never "publisher broken". This ambiguity cost
  real debugging time before the change.
- `PlanMessage` round-trips through camelCase JSON with `docIds` intact. Had that list
  deserialized to null, `DeletePlanAsync` would have deleted nothing and still acked — a silent
  no-op worth guarding.

Demo 2 behaviour is unchanged. `Program.cs` gains a DI block, one parameter per endpoint lambda,
and three publish hooks; `RedisRepository.cs`, `PlanFlattener.cs`, `PlanMerger.cs`, `ETag.cs`,
and `schema.json` are untouched, and the GET endpoint is unmodified.
