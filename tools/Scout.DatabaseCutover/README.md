# Scout database selection and cutover

Scout now reads a persistent bootstrap file at `%LOCALAPPDATA%\Scout\storage.json`. It records the database mode/path and original logical settings/workspace roots. Normal launches, shortcuts, and taskbar entries use the same choice. Without this file, the existing standalone default remains. Invalid selections, missing selected databases, unsupported versions, and unfinished switches fail closed; they do not create a new empty database or silently reopen the old one. Settings shows the active database location.

The file contains paths and a cutover ID, never credentials. Protected credential bytes remain in SQLite. Ripley and Tucker use the same selector and shared credential adapter. After selection of Doggo, a removed or unavailable key cannot fall back to a stale standalone or loose-file copy.

`SCOUT_DATA_ROOT` selects an isolated bootstrap directory. `SCOUT_DOGGO_DATABASE`, `SCOUT_SETTINGS`, and `SCOUT_WORKSPACES` remain process-local test overrides and do not rewrite the persisted selection. Normal production launches need no environment override. Existing logical document paths must be preserved even when the physical database moves.

## Build and isolated use

Use Scout's pinned SDK, with matching sibling Tucker and Ripley checkouts for the shared implementation and companion tests. Build this tool directly; it does not install or launch any app:

```powershell
dotnet build tools/Scout.DatabaseCutover -c Release -m:1
```

Without `--allow-live`, physical inputs/output must be under `.build` or the process's temporary directory. Canonical Scout, Episcout, and Doggo live locations are rejected. Use **copies** of existing offline snapshots. Do not reuse an old rehearsal database as a live replacement.

```powershell
$tool = 'tools/Scout.DatabaseCutover'
$root = Join-Path $PWD '.build/cutover-rehearsal-new'
# First seed $root/Scout/scout.db and $root/Doggo/doggo.db from offline snapshots.
# These are the ORIGINAL logical roots stored in the source database, not relocated folders:
$settingsRoot = "$env:LOCALAPPDATA\Scout\UI-Trial"
$workspacesRoot = Join-Path $settingsRoot 'Workspaces'

dotnet run --project $tool -c Release --no-build -- prepare `
  --scout-root "$root/Scout" --source "$root/Scout/scout.db" `
  --target "$root/Doggo/doggo.db" --output "$root/prepared" `
  --settings-root $settingsRoot --workspaces-root $workspacesRoot

# Review prepared/cutover-plan.json and migration-report.json before applying.
dotnet run --project $tool -c Release --no-build -- apply --plan "$root/prepared/cutover-plan.json"
dotnet run --project $tool -c Release --no-build -- status --scout-root "$root/Scout"
```

Preparation uses SQLite online backups, including committed WAL data, and verifies documents, report rows, protected credentials, integrity and existing peer tables. It refuses occupied Scout destinations, inconsistent logical roots, existing output directories, junctions and symbolic links. The plan records logical fingerprints of the source, target, and prepared copies. Any intervening database or selection change requires a fresh plan.

Apply imports **only Scout's tables** in a transaction. It does not replace the shared database file or rewind Ripley/Tucker data. The transaction also records `doggo_cutover` version 1 and a durable `doggo_cutovers` receipt. A flushed `storage.pending.json` marker blocks Scout startup and credential lookup until activation completes. Apply retires the old standalone database by marking it read-only, atomically saves the new selection, then removes the pending marker. The original database and backups remain available.

Updated app entry points hold shared `.access` leases; the tool requires exclusive leases for the selector and both databases. A separate read-only file handle refuses an open legacy standalone writer and prevents one starting during the switch. Live mode additionally refuses any running Scout, Tucker, Ripley, Reel, or Episcout process. Never delete lease files to bypass an active lease.

## Recovery

If interrupted, use the **same unmodified plan**:

```powershell
dotnet run --project $tool -c Release --no-build -- recover --plan "$root/prepared/cutover-plan.json" --action resume
# Or undo an unused/unfinished switch:
dotnet run --project $tool -c Release --no-build -- recover --plan "$root/prepared/cutover-plan.json" --action rollback
```

Resume recognizes a committed import and finishes activation without copying the data again. Rollback removes only the unchanged imported Scout tables, restores the prior selector and source file attributes, and keeps a cutover audit receipt. New peer data is preserved. Rollback refuses any changed Scout data/schema, changed source, modified backup, foreign pending operation, or unexpected selector. Once Scout has been opened and initialized its inbox or saved new work, rollback can require a separate reviewed migration; it is not a general restore-over-new-work command.

Isolated tests can inject an interruption with `apply --interrupt-at before-import|during-import|after-import|before-selection|after-selection`. This is refused with `--allow-live`. The core tests cover rollback and resume at every boundary, stale plans, selectors, file locks, changed credentials, and concurrent peer preservation. Companion tests exercise both actual Ripley and Tucker credential entry points with synthetic DPAPI-protected keys; no provider calls or real credential disclosure are needed.

After a successful isolated cutover, the existing handoff harness can test the stored selection in separate processes:

```powershell
dotnet build tools/Scout.HandoffRehearsal -c Release -m:1
dotnet run --project tools/Scout.HandoffRehearsal -c Release --no-build -- selection-handoff $root
dotnet run --project tools/Scout.HandoffRehearsal -c Release --no-build -- selection-status $root
```

These modes require `.build/cutover-rehearsal-*`, verify the selector points inside that directory, and use only synthetic media. They never process the media referenced by copied historical records.

## Later live rollout

This implementation has been exercised only against isolated copies. A future live rollout must close all apps, install compatible Scout/Ripley/Tucker builds, prepare **fresh** backups of current live data, review the resulting plan, then apply it with explicit `--allow-live`. Supply that flag to live preparation and recovery as well. Existing logical roots must match current Scout, including an Episcout root if retained from an older installation.

The tool never installs executables, changes shortcuts, stops processes, starts models, processes media, or copies plaintext credentials. Keep the old app/database backups until normal launches, credentials, history, and both handoff directions have been checked. `.build` contains private snapshot data and remains excluded from Git.
