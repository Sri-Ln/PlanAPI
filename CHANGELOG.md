# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-06-07

### Added

- `POST /v1/plan` with draft-07 JSON Schema validation, returning `201 Created`, `Location`, and `ETag` headers.
- `GET /v1/plan/{objectId}` returning the reassembled plan with `ETag`; honors `If-None-Match` and returns `304 Not Modified` on match.
- `DELETE /v1/plan/{objectId}` with recursive cleanup of all flattened child records.
- Flattened Redis storage: each nested object stored under `{objectType}:{objectId}`; parents hold reference strings to children.
- Canonical (sorted-key) SHA-256 ETag derived from the reassembled document.

[Unreleased]: https://github.com/Sri-Ln/PlanAPI/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Sri-Ln/PlanAPI/releases/tag/v0.1.0
