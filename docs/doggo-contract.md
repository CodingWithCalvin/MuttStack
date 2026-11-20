# Doggo database contract

Each app owns all of its implementation, tests, packaging and workflows in its own repository. There are no sibling project references, source links, shared private packages, cross-repository workflows or reads of another app's private configuration. Similar adapter code is independently maintained; compatibility is defined by the database contract, not a shared library.

## Storage and ownership

The default integration point is `%LOCALAPPDATA%/Doggo/doggo.db`, a local SQLite database for the current Windows user. Table prefixes provide logical schemas, not access-control boundaries. `doggo_migrations(component, version, applied_utc)` tracks each component independently. Writers use short transactions, foreign keys, WAL and a bounded busy timeout. An app migrates only its own tables and the shared components it understands; unsupported newer component versions are refused. This refactor changes no deployed schema or payload version.

| Contract | Tables / owner |
| --- | --- |
| Ripley to Tucker, `handoff=1` | `handoff_batches`, `handoff_events`; producer offers, recipient responds |
| Tucker to Scout, `handoff_routes=1` | `handoff_routed_batches`, `handoff_routed_events`; explicit source, recipient, contract and version |
| Ripley completion evidence | `ripley_completed_sources`, `ripley_reconciliations`; Ripley writes, Tucker may read the published evidence |
| App state | `ripley_`, `tucker_`, `scout_` tables; only their owner writes |
| Shared TVDB credential read | Exactly one `scout_documents` row of kind `credentials.dpapi`; Scout writes, companions read only |

SQLite journals and the adjacent `.access` maintenance lease belong to database storage. They contain no app settings or handoff messages. A running app holds a shared storage lease; cutover maintenance requires an exclusive lease. Scout alone owns its persistent storage selector and legacy import logic. Companions never follow `Scout/storage.json` or fall back to a standalone Scout database.

## Durable handoffs

Batch IDs are stable GUIDs. Offers contain immutable JSON and its SHA-256 digest over exact UTF-8 bytes. Retrying the same ID and payload is idempotent; changing its payload is rejected. The legacy Ripley channel retains version 1. The routed channel uses `SourceApp=tucker`, `TargetApp=scout`, `Contract=tucker.scout.outputs`, `Version=1`. File records include absolute paths, sizes and UTC modification times, plus available series/disc metadata. These fingerprints detect replacement but are not full-media hashes.

The recipient validates the batch and media, then commits its own waiting work, receipt and shared Accepted response atomically. Failure rolls back the entire acceptance. Decline creates a durable response without starting work. Sender restart/polling reads the response even if the sender was closed at acceptance. Only the recipient writes a response; private queues are never manipulated by a peer. Processing remains a separate user action.

## Credentials and compatibility tests

TVDB credentials stay protected with Windows DPAPI for the current account. Companion readers require a supported Doggo/Scout schema and exactly one authoritative credential document. Missing, ambiguous or removed credentials fail explicitly; old files are never searched. No plaintext key is persisted by the adapter.

Each repository tests its own adapter against synthetic database messages and responses, including retries, restart acknowledgement, schema rejection and rollback. Scout's rehearsal uses frozen contract fixtures and its real receiver in separate processes; it does not compile or load Tucker. `eng/check-independence.ps1` rejects build inputs outside the repository. Keep compatibility by evolving documented database contracts and each app's fixtures, not by adding a shared source dependency.

Installers package code only. They do not migrate a database, change Scout's storage selection, include credentials/media, or launch processing. See [releases.md](releases.md).
