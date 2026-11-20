# Tucker

Independent Windows WPF compression companion to Ripley and Scout. Amber/copper shell and a dachshund mascot with a copper flat cap and Tucker name tag.

## Run

Open `artifacts\app\Tucker.exe`. Keep the application folder together. Requires Windows and the .NET 9 Desktop Runtime. The local packaged build includes a separate HandBrakeCLI copy and its license documentation; it does not require Ripley to run or remain installed.

1. Open **Output & settings**, choose the compressed-output base directory, installed GUI/custom preset, decoder and naming preferences, then **Save preferences**.
2. Choose **+ New Tucker** and add any combination of existing-rip folders and inbox batches. Tucker names the run automatically from the selected sources, using series and season metadata when available and folder names otherwise. The page heading shows the name as sources are selected; preview refines it with the unique file count before **Create Tucker**. There is no run-name field to fill in. **Add inbox batches** opens Inbox, where multiple waiting rows can be selected and added together. Return and add more sources as needed; there is no fixed folder or batch-count limit.
3. **Preview combined run** resolves every file from its own metadata, combines overlapping sources once, and shows proposed output paths. Inbox metadata takes precedence over a folder import of the same file. Edit sources to invalidate the old preview, or preview again after changing output preferences.
4. **Create Tucker** saves the entire selection as one waiting run in **Tucker history**. Each inbox batch receives its own acknowledgement in the same transaction as the run. If any batch cannot be accepted, the whole save is rolled back. **Create & add to active processing** explicitly includes the new run in processing already underway.
5. Select a saved Tucker to see its files, frozen settings, status and progress. **Start this Tucker** processes only that run. **Start all**, overall progress, pause and stop are in the activity footer on every screen. Runs finish sequentially. Completed runs remain in history after reopening.

To extend a saved Tucker, select it in history and choose **Add more sources**. Combine more folders and inbox batches, then preview the additions. This works for waiting, running, paused, and completed runs. **Add as waiting** saves the files to that same run for a later Start; **Add to active processing** explicitly includes them in processing already underway. Pausing or stopping disables active joining while still allowing waiting additions. If processing finishes during preview, the additions can be saved as waiting without restarting it.

The target run stays fixed while collecting sources, including when navigating to Inbox or another history entry. New files use the current output and preset preferences shown in their preview; existing files retain their frozen settings, status, and output. All appended files and inbox acknowledgements commit together. **Cancel additions** discards only the unsaved selection and returns to the original run.

The sidebar contains **+ New Tucker**, **Inbox**, and **Tucker history**; there is no separate Compression Queues destination. Starting another New Tucker clears the unfinished form, while offered inbox batches remain waiting until a run is saved or they are declined. Existing saved queues appear as individual history entries without changing their memberships or output settings.

Right-click a run in history to **Archive** or **Delete** it. Archived runs retain all their files and settings, disappear from active history and processing totals, and are excluded from **Start all**. Open **Archived (N)** beside the history heading to review them; right-click **Restore** or choose **Restore Tucker** to return a run to history without starting it. Restore before adding sources. **Delete** asks for confirmation, then removes the saved run and its pending work; raw files, completed media, metadata sidecars and inbox receipts stay intact. Archive and Delete are unavailable for a run whose files are currently encoding or scheduled; pause after the current file or stop first. Unscheduled runs can be managed while another run processes.

Raw media is never overwritten, renamed, or deleted by Tucker. Output publication uses an atomic, non-overwriting move with collision suffixes. Completed output and import receipts survive deleting runs. A source path/size/UTC-modified signature prevents repeated ordinary imports, including after deletion. **Inbox Ã¢â€ â€™ Re-accept selected** explicitly allows accepted batches to be processed again. Interrupted publication becomes **Needs review** and remains protected as unfinished work; inspect its output before deleting that run and re-accepting the batch.

## Presets and naming

Tucker reads installed HandBrake GUI presets/settings without modifying them. Preview freezes every preset setting into an independently named preset; its JSON is also stored in Tucker's database. Queued jobs retain it after preference changes. NVDEC is decoding, not NVENC encoding. Tucker does not change encoder, speed, RF, filters, scaling or audio options for performance.

Folder and filename formats support `{series}`, `{series_name}`, `{year}`, `{tvdb_id}`, `{season}`, `{volume_number}`, `{disc_number}`, `{disc_label}`, `{date}`, numeric padding such as `{season:00}`, and escaped `{{`/`}}`. Retired `disc`, `disc_desc` and `volume` aliases are rejected. Filename formats also support `{filename}` (the current prefixed source stem); the selected preset supplies the extension. The default `{filename}` retains existing names.

**Folder preview** beneath the relative folder format shows the full resolved path as you edit the format or output base. It uses the same folder formatter as compression, including token padding, escaped braces and Windows filename cleanup. Choose a source from your previewed rips, staged inbox batches or selected saved run to see its own metadata; the selected preview file is preferred when opening Settings. With no available source metadata, it uses clearly labeled example values. Missing real metadata and invalid formats appear inline instead of substituting sample values. The sample is read-only: it creates no folders and changes no saved job settings. Preview the run again to apply updated preferences to new work and validate the actual destination.

For example: `{series} {{tvdb-{tvdb_id}}}\Season {season:00}`. Existing Ripley prefixes are preserved. Metadata comes from each raw rip's saved manifest; older folders can supply explicit season/disc markers. Series details come from each fileÃ¢â‚¬â„¢s saved metadata or inbox handoff. When a required naming token has no value, preview identifies the source that needs metadata or a format without that token. No global series or disc values are applied across batches. TVDB search is not exposed in this first Tucker UI.

## Independent storage

Production database: `%LOCALAPPDATA%\Doggo\doggo.db`. Tucker creates only `doggo_`, `handoff_` and `tucker_` tables. Its presets/logs use `%LOCALAPPDATA%\Tucker`. Scout and Ripley databases, settings and queues are never migrated or opened automatically. A database-scoped mutex prevents two Tucker encoders using the same library.

For isolated testing: `Tucker.exe --state C:\some\test-directory`. This places both database and scratch settings under that directory. It never starts processing on restoration.

## Inbox and Scout handoffs

Import a version-1 JSON handoff through **Inbox Ã¢â€ â€™ Import handoff**. It stays Offered while staged and previewed in New Tucker. All selected batches are acknowledged only when their combined run is saved; run creation and every acceptance commit in the same SQLite transaction. Export an acceptance receipt when needed. See [the handoff contract](docs/handoffs.md) and [database design](docs/database.md).

The Inbox sidebar badge counts batches awaiting review, refreshes every five seconds from any screen, and appears on startup for handoffs received while Tucker was closed. Accepting or declining updates it immediately. It disappears when nothing is waiting; accepted and declined batches remain in inbox history.

To process an accepted batch again, select it in **Inbox** and choose **Re-accept selected**. Multiple accepted batches, waiting batches and existing-rip folders can be combined in a new Tucker or added to an existing run. Preview marks eligible sources **Ready to reprocess** and files with unfinished jobs **Already queued**. Unfinished work is skipped across all runs, including archived runs; resume, restore or delete the old run as appropriate. Re-accepting can recover deleted runs or deliberately recompress completed files, including historical Ripley completions, using current output and preset preferences. Existing outputs receive collision protection, and the original manifest, acceptance timestamp and receipt remain unchanged. Nothing is queued until the preview is saved, and ordinary **Create Tucker** leaves the work waiting for Start.

Folder imports can also be retried without an inbox batch. In the combined preview, select the **Already imported** rows you want and choose **Reprocess selected**. This status records an earlier import, not proof that encoding finished; it can remain after deleting a waiting run. Only the selected files become **Ready to reprocess**. **Already queued** files still have unfinished work and cannot be duplicated. **Reset reprocessing** clears the per-file choices; changing sources or generating a fresh preview also clears them. Save with **Create Tucker** or **Add as waiting** to commit the selected work. These choices apply only to that preview, keep the current output/preset settings, and never erase import history, inbox receipts or completed media.

**Ripley sending is connected.** Use Send to Tucker on a newly completed Ripley run; it appears in TuckerÃ¢â‚¬â„¢s inbox. Existing completed compression is reconciled through a Ripley-owned completion ledger and skipped during Tucker import. Tucker Ã¢â€ â€™ Scout receiving is implemented for isolated testing: select a saved run and choose **Send to Scout** to offer its newly completed outputs. Scout accepts them into waiting drafts, and Tucker picks up the durable response on startup or refresh. Scout must use the same Doggo database to receive these batches. See [the Scout handoff workflow](docs/scout-handoffs.md). Tucker retains compatible `.ripley.json` output sidecars for downstream metadata.

## Development and verification

`scripts\build.ps1 -HandBrakeCli <existing HandBrakeCLI.exe>` publishes to the gitignored `artifacts\app` folder. `scripts\test.ps1` runs inherited media/preset/naming safety checks, Tucker SQLite/queue tests, and WPF workflow checks with fake CLI tools and temporary media. It never performs a real encode. Windows-provided `winsqlite3.dll` is accessed through a small parameterized wrapper; no database server or third-party NuGet packages are required.

Compression/preset/naming components were adapted from Ripley at recorded commit `f6ba719`; the existing MIT license is retained. Tucker owns its local `src/Doggo.Handoffs` adapter. No other repository references or source-links it. Integration is exclusively through the [Doggo database contract](docs/doggo-contract.md).

Tucker reads protected TVDB credentials only from its selected Doggo database when needed. It never reads Scout's storage selector or standalone credentials. The current Tucker handoff UI does not require TVDB credentials. Database maintenance uses a database-scoped storage lease.
## Installers and CI

`eng/test.ps1` validates this repository independently. `eng/package.ps1` builds a self-contained Windows x64 MSIX and portable ZIP in a new output directory. GitHub Actions runs tests and packaging per repository; version tags additionally sign and create a draft release when signing credentials are configured. See [Windows packaging and signing](docs/releases.md).
