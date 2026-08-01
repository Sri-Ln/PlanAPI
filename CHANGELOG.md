# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] - 2026-08-01

### Added

- RabbitMQ producer: every successful Redis write (POST, PATCH, DELETE) publishes to a durable queue with persistent messages and publisher confirms.
- Background consumer (`IndexerService`) that indexes plans into the Elasticsearch `demo` index, using `prefetch=1` and manual acknowledgement after the Elasticsearch write succeeds.
- Parent-child flattening (`EsFlattener`): a nested plan becomes one document per `objectId`, each carrying a `plan_join` relation and routed by the root plan's `objectId` so the whole tree shares a shard.
- PATCH propagation to the index: the merged plan is published, so an update re-indexes the whole tree and cannot drift from Redis.
- Cascaded delete into Elasticsearch: DELETE publishes every descendant document id, removed using the root plan's routing value.
- Elasticsearch, Kibana, and RabbitMQ services in `docker-compose.yml`.
- Configuration keys `RabbitMq:HostName`, `RabbitMq:Queue`, `Elasticsearch:Uri`, and `Elasticsearch:Index`.

### Notes

- Redis remains the source of truth; Elasticsearch is a derived store and is never read authoritatively.
- A failed publish is logged and does not fail the request, since Redis has already committed.
- A message that always fails to index is requeued indefinitely; there is no dead-letter queue.

## [0.2.0] - 2026-07-10

### Added

- `PATCH /v1/plan/{objectId}` with conditional merge update: `If-Match` required, returning `428` when missing and `412` when stale.
- Merge semantics: objects deep-merged, `null` deletes a member (RFC 7386), and `linkedPlanServices` upserted by `objectId`.
- OAuth 2.0 bearer-token security validating Google-issued RS256 ID tokens against Google's JWKS.
- Schema validation applied to the PATCH-merged result, not just the request body.

## [0.1.0] - 2026-06-07

### Added

- `POST /v1/plan` with draft-07 JSON Schema validation, returning `201 Created`, `Location`, and `ETag` headers.
- `GET /v1/plan/{objectId}` returning the reassembled plan with `ETag`; honors `If-None-Match` and returns `304 Not Modified` on match.
- `DELETE /v1/plan/{objectId}` with recursive cleanup of all flattened child records.
- Flattened Redis storage: each nested object stored under `{objectType}:{objectId}`; parents hold reference strings to children.
- Canonical (sorted-key) SHA-256 ETag derived from the reassembled document.

[Unreleased]: https://github.com/Sri-Ln/PlanAPI/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/Sri-Ln/PlanAPI/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Sri-Ln/PlanAPI/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Sri-Ln/PlanAPI/releases/tag/v0.1.0
