# Scout Doggo storage (staged support)

Normal startup uses `%LOCALAPPDATA%\Scout\scout.db` until the cutover tool saves `%LOCALAPPDATA%\Scout\storage.json`. Later launches use that persisted selection. Installing this code does not import data, change the live database, or restart an existing Scout. Do not cut over while Scout is processing.

The optional shared location is `%LOCALAPPDATA%\Doggo\doggo.db`. Scout consumes `doggo` version 1 and owns `scout` version 1 in `doggo_migrations`. Its tables are `scout_documents`, `scout_file_results`, and `scout_imports`. Other app components are ignored rather than migrated. Scout leaves legacy `handoff_batches`/`handoff_events` and all `tucker_*`/`ripley_*` tables alone. The optional inbox consumes the separate `handoff_routes` version 1 channel and owns `scout_handoffs` version 1 receipts/file mappings. Table prefixes are ownership conventions, not access-control boundaries.

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

For an explicit isolated desktop test, `SCOUT_DOGGO_DATABASE` can name the absolute path of the migrated copy. Keep the existing logical settings/workspace roots: document keys deliberately retain their original paths. This opt-in environment variable is never set by migration or normal startup. Only test the desktop after the running Scout is idle and closed; its single-instance guard remains in place. The separate [cutover tool](../tools/Scout.DatabaseCutover/README.md) now implements preparation, activation and recovery. It has only been exercised with isolated copies.

## Before live cutover

Take fresh backups and repeat validation with the apps idle. Rehearsal data is a point-in-time snapshot, not a synchronization mechanism. Ripley and Tucker read protected credentials directly from their configured Doggo database. Scout alone reads its persisted storage selector. Missing or removed shared keys never fall back to standalone copies.

Tucker-to-Scout handoffs now use separate recipient-aware tables, leaving the installed Ripley-to-Tucker v1 contract unchanged. Tests use isolated copies. Scoutâ€™s Inbox creates exact-file draft workspaces and its acceptance receipt in one transaction; no processing starts on acceptance. Standalone startup does not initialize shared handoff tables. An unfinished cutover blocks Scout startup until recovered; active database maintenance holds an exclusive lease that also blocks companion credential readers. See [the separate-process handoff rehearsal](../tools/Scout.HandoffRehearsal/README.md).

Scout builds independently using its own adapter in `Scout.Core/Doggo`. No sibling checkout, shared source links or companion assemblies are required. See the [database contract](doggo-contract.md) and [Windows packages](releases.md).