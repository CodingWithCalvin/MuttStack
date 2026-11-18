# Scout Doggo storage (staged support)

Normal startup still uses `%LOCALAPPDATA%\Scout\scout.db`. Installing this code does not import data, change the live database, or restart an existing Scout. Do not cut over while Scout is processing.

The optional shared location is `%LOCALAPPDATA%\Doggo\doggo.db`. Scout consumes `doggo` version 1 and owns `scout` version 1 in `doggo_migrations`. Its tables are `scout_documents`, `scout_file_results`, and `scout_imports`. Other app components are ignored rather than migrated. Scout does not consume or write `handoff_*`, `tucker_*`, or `ripley_*` tables. Table prefixes are ownership conventions, not access-control boundaries.

Component initialization is transactional. Connections use WAL, foreign keys, synchronous FULL, and a five-second default lock wait. Unsupported versions of consumed components are rejected. Doggo's global `user_version` is not used for app versioning. Legacy standalone Scout databases keep their existing format.

## Isolated rehearsal

```powershell
dotnet run --project tools/Scout.DatabaseRehearsal -- `
  "$env:LOCALAPPDATA\Scout\scout.db" `
  "$env:LOCALAPPDATA\Doggo\doggo.db" `
  ".build\doggo-rehearsal-new"
```

Use a new output directory. Pass `-` instead of the Doggo path to test against a fresh database. This command:

1. Opens the live sources read-only and uses SQLite's online backup API, including committed WAL content. SQLite may create/open its lock and WAL sidecars; Windows needs directory access for those even with a read-only connection.
2. Creates `scout.snapshot.db`, `doggo.before.db` (when present), and `doggo.migrated.db` in the rehearsal directory.
3. Imports documents and file-result rows in one transaction, preserving virtual keys, kinds, content bytes, ordinals, source paths, and serialized payloads exactly. DPAPI bytes are copied without decryption or re-encryption.
4. Checks content digests, SQLite integrity, foreign keys, and the existing peer tables/migration records, and saves `migration-report.json` without credentials or dialogue.

The same unchanged snapshot can be imported idempotently. Changed or occupied Scout destinations are refused, not merged. Existing backups are never overwritten. The import API refuses the canonical live Scout and Doggo database paths. Rehearsal snapshots contain private data and encrypted credentials; `.build` is excluded from Git.

For an explicit isolated desktop test, `SCOUT_DOGGO_DATABASE` can name the absolute path of the migrated copy. Keep the existing logical settings/workspace roots: document keys deliberately retain their original paths. This opt-in environment variable is never set by migration or normal startup. Only test the desktop after the running Scout is idle and closed; its single-instance guard remains in place. The live cutover workflow is intentionally not implemented yet.

## Before live cutover

Take fresh backups and repeat validation with the apps idle. Rehearsal data is a point-in-time snapshot, not a synchronization mechanism. Reconcile read-only consumers before switching: Tucker currently reads `Scout\scout.db` / `documents` to obtain Scout credentials. The preserved ciphertext is compatible, but Tucker's lookup must be coordinated so later key changes are not read from a stale database. This change does not modify Tucker.

Tucker-to-Scout handoffs are not part of this storage change. The installed v1 offer contract is Tucker-only and its inbox routing is not multi-consumer. An additive versioned contract and coordinated consumer filtering are required before Scout-targeted offers can be enabled.
