# Mutt Stack storage â€” Tucker implementation

One local SQLite file, WAL mode, foreign keys, synchronous FULL and a bounded busy timeout. Work runs outside database transactions. Prefixes are ownership conventions, not security boundaries. Windows file permissions protect access.

| Component | Tables |
| --- | --- |
| doggo | `doggo_migrations(component, version, applied_utc)` |
| handoff | `handoff_batches(id, digest, payload, status, offered_utc, accepted_utc)`, `handoff_events` |
| handoff_routes (v1) | `handoff_routed_batches`, `handoff_routed_events` (recipient-aware Scout channel) |
| tucker_handoffs (v1) | `tucker_scout_outbox`, `tucker_scout_sent_files` |
| scout_handoffs (v1, Scout-owned) | `scout_handoff_receipts`, `scout_handoff_files` |
| doggo_cutover (v1, Scout tool) | `doggo_cutovers` (durable cutover receipts) |
| tucker | `tucker_jobs`, `tucker_imported_sources`, `tucker_receipts`, `tucker_presets`, `tucker_settings`, `tucker_archived_runs` |

Initialization and migration version checks execute under BEGIN IMMEDIATE. Doggo and handoff remain at version 1. Tucker version 2 adds `tucker_archived_runs(run_id)` without rewriting existing jobs, settings, presets or receipts. Initialization installs versions 1 and 2 transactionally, and existing version 1 databases receive the additive archive table on startup. Versions newer than each supported component cause a safe refusal; unrelated app versions do not block Tucker. Later migrations must be ordered, component-specific and additive where possible. Do not modify deployed DDL to evolve databases; introduce a new component version.

Archive and restore persist run IDs with the job state in one transaction. Archived jobs are excluded from processing until restored. Deleting a run removes its job rows and archive entry only; imported-source signatures, receipts, shared handoff events and media stay intact. Archive/delete changes preserve references to any unrelated active job, and the manager rejects changes to runs with active or scheduled files. A failed save is not adopted in memory.

The acceptance transaction writes Tucker's jobs and deduplication ledger, inserts its unique receipt, conditionally changes Offered to Accepted and records acceptance. A stale/declined/changed handoff rolls back all those changes. Accepting does not schedule an encode. Frozen preset JSON is committed before acceptance and is independently retained in tucker_presets. Exported CLI preset files can be recreated from it.

Explicit inbox re-acceptance adds new Tucker jobs while retaining the original `tucker_receipts`, shared Accepted status, timestamp and events. The transaction revalidates the existing batch and receipt before writing jobs; it never resets shared status or deletes import signatures. Reprocessing intent belongs only to the local preview and requires an explicit inbox action. Unfinished jobs across active and archived runs continue to block duplicates. This needs no schema migration or changes to Ripley or Scout.

Folder-preview reprocessing uses an explicit, non-serialized per-file selection. `tucker_imported_sources` means a source was once queued, not that compression completed. Normal imports retain the historical deduplication policy, while **Reprocess selected** can bypass it for chosen sources after fingerprint validation. The save still checks unfinished jobs, allocates fresh job IDs and collision-safe names, and commits atomically. No inbox receipt is invented for a folder import, and a saved job never retains permission to bypass deduplication in future imports.

Jobs use typed identity/order/queue columns and a versioned application payload. Tucker serializes its own writers through one desktop instance; other apps never update its private tables. Each repository owns its database adapter; the published integration API is the [SQLite contract](doggo-contract.md), not a shared code library.

Ripley now owns `ripley_completed_sources` and `ripley_reconciliations` (component version 1), storing verified historical completion evidence. Tucker reads that published completion ledger to prevent duplicate imports. Ripley preferences and operational history remain in its existing JSON store for this staged cutover; only completion annotations and handoff status are added. Full Ripley history migration is separate work. A coordinated Scout cutover must back up via SQLite's backup API, preserve Scout's virtual document keys and DPAPI credential bytes, migrate idle Ripley state explicitly, and install the updated read-only credential integration. Do not combine old and new writers until compatibility is established. Backing up only doggo.db while a live WAL exists is not a valid backup procedure.

This is a per-Windows-user local database. The Inno Setup installer packages each app independently; it supplies no database service, schema-permission system or cross-app update coordinator. Uninstalling an app leaves the shared database intact.

The additive [Tucker to Scout workflow](scout-handoffs.md) is tested with isolated databases. Scout owns its receipt and file tables and commits acceptance with its workspace documents. The legacy Ripley to Tucker tables and versions remain unchanged.

Updated Tucker holds a shared storage lease for its database while open. Scout cutover maintenance requires an exclusive lease, so a running Tucker blocks it. Credential lookup reads only the selected Mutt Stack database; it does not inspect Scout's storage selector or fall back to standalone keys.