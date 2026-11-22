# Ripley

[TMDB support, provider-aware paths and compatibility](docs/tmdb.md).

A Windows desktop app for ripping discs with MakeMKV and sending completed raw files to Tucker for compression. The forest-green shell matches the Doggo app collection. Raw files and disc history are retained.

## Quick start

1. Open `artifacts\app\Ripley.exe` after building.
2. Choose **New Ripley**, insert and inspect a disc, select its series, and set the raw folder and filename format. Ripley reads saved TMDB or TVDB credentials from the configured Doggo database.
3. Choose **Rip disc**. MakeMKV saves raw files and per-file metadata. The minimum title length defaults to 120 seconds; all qualifying titles are ripped.
4. When the run completes, choose **Send to Tucker**. Tucker can be closed. Delivery creates an inbox offer and locks the completed run against duplicate work.
5. Open Tucker's **Inbox**, review the output and preset there, and add the batch to its waiting queue. Start compression in Tucker when ready.

Ripley has no local compression queue, HandBrake configuration, or compression controls. Settings contains only MakeMKV and the saved TMDB/TVDB credential location. The ripping file list still shows title progress, filenames and bytes written.

Drives refresh on startup and Windows media notifications. Empty drives remain listed; unchanged media preserves manual values. Refresh waits during an active disc operation and never starts a rip. **Refresh** remains available for manual checks.

## Folder formats and filenames

| Token | Example |
| --- | --- |
| `{series}` or `{series_name}` | `iCarly` |
| `{year}` | `2007` |
| `{provider}` | `tmdb` or `tvdb` |
| `{id}` / `{series_id}` | Selected provider series ID |
| `{tmdb_id}` | `5371` (TMDB only) |
| `{tvdb_id}` | `82493` (TVDB only) |
| `{season}` | `1` |
| `{season:00}` | `01` |
| `{volume_number}` / `{volume_number:00}` | `2` / `02` from `V2` |
| `{disc_number}` / `{disc_number:00}` | `2` / `02` from `D2` |
| `{disc_label}` | `ICARLY_S1_V1_D1` |
| `{date}` | `2026-11-06` |
| `{date:yyyyMMdd}` | `20261106` |

**Auto from disc label** fills the editable **Season**, **Volume number**, and **Disc number** fields from explicit markers. `ICARLY_S1_V2_D2` means season 1, volume 2, disc 2. Fill in missing values or override detected values for the current disc. With auto detection enabled, selecting another disc clears values that cannot be detected; disc number alone is never mapped to a season. Disable auto detection to keep manual values between discs. `{disc_label}` is the full label; `{disc_number}` is only its disc number. The token names are `{disc_label}`, `{disc_number}`, and `{volume_number}`; `{disc}`, `{disc_desc}`, and `{volume}` are not supported.

Use `\` to create subfolders. Use `{{` and `}}` for literal braces. Token names are case-insensitive. Missing values and unknown tokens are errors; choose the series, supply the missing disc values, or remove those tokens. For workflows without a series, `{disc_label}` is sufficient.

Default raw format:

```text
{series} ({year}) {{{provider}-{id}}}\{disc_label}
```

Set **Filename prefix** to:

```text
{volume_number}_{disc_number}_
```

With volume 2, disc 2, and MakeMKV filename `ICARLY_S1_t00.mkv`, Ripley writes `2_2_ICARLY_S1_t00.mkv`. The folder format `{series} {{tvdb-{tvdb_id}}}\Season {season:00}` places it in `iCarly {tvdb-82493}\Season 01` under the raw base folder. Compressed output keeps the prefixed name, using the container extension from your preset. Padded numbers also work, for example `{volume_number:00}_{disc_number:00}_`. A blank prefix preserves MakeMKVÃ¢â‚¬â„¢s filename. The prefix supports the same tokens as folder formats, but cannot contain subfolders.

Ripley applies filenames after MakeMKV confirms a complete rip and before it can be sent to Tucker. Each disc has a separate `.ripley-rips\<session>` folder for its log, metadata, and incomplete files. Completed raw MKVs are moved into the requested folder without overwriting existing files. The manifest records the original and final filenames; Tucker receives both names in its inbox manifest. Interrupted rips stay in their session folder and are excluded from compression imports.

## History and recovery

Rip sessions are saved in `%LOCALAPPDATA%\Ripley\history.json`. **New Ripley** retains folder preferences and the selected series while clearing disc-specific values. Selecting completed history restores the exact raw files for sending to Tucker. A disc operation prevents switching sessions until it finishes or is cancelled.

Right-click a history entry to **Archive** or **Delete** it. **Archived** opens the archive, where **Restore** returns an entry to history with its previous status and handoff lock intact. Archived runs can be reviewed but must be restored before ripping or sending to Tucker. Deleting an entry requires confirmation and removes only its history record: raw files, disc metadata, saved outbox manifests and Tucker requests remain. History management is disabled during a disc operation or handoff delivery.

**Cancel disc operation** stops MakeMKV and keeps files already written. Incomplete files remain in the session folder and cannot be handed off. Interrupted sessions reopen as interrupted and never restart automatically. **Written** indicates MakeMKV moved to another file; **Saved** confirms the complete rip and filename publication succeeded.

Legacy compression queue files, frozen presets and dormant settings are preserved. Ripley reads legacy queue sources only to protect previously queued history from duplicate delivery; it never resumes or rewrites that queue. Reconciled runs retain their completion evidence and show **Compression completed**. New compression is managed in Tucker.

On first launch after an older Reel installation, saved settings, queue records and frozen presets are copied to the Ripley data folder without deleting the originals. Existing Reel manifests and sidecars remain readable. Media is never renamed as part of the upgrade.

## Tucker inbox handoff

Ripley saves an immutable manifest in its `tucker-outbox` folder and offers it in `%LOCALAPPDATA%\Doggo\doggo.db`. Tucker's acceptance is committed atomically with its queue. Ripley reads acknowledgement on startup and every five seconds while idle, including acceptance that happened while Ripley was closed. Delivery itself starts no compression.

Failed delivery exposes **Retry send to Tucker** and reuses the same ID. Offered, accepted and declined runs stay locked against duplicate work. History distinguishes **Awaiting Tucker**, **Accepted by Tucker** and **Declined by Tucker**. Raw files remain in place.

After an interrupted send, Ripley restores and saves the run's lock from its local outbox before opening the shared database. If the database is unavailable, the original request stays frozen and can be retried when storage is available; a retry never creates a second offer or reopens an accepted request.

Ripley owns `src/Ripley.Doggo`; all project references stay inside this repository. App integration uses the [Doggo database contract](docs/doggo-contract.md). Operational Ripley history and preferences remain local JSON.

`tools/Ripley.Reconcile` validates completion sidecars, source size/mtime, output presence and media headers, then can explicitly record evidence in `ripley_completed_sources` and annotate history. Back up first and supply the backed-up history SHA256 when applying. This is evidence reconciliation rather than a full decode test; it creates no jobs and moves no media. Tucker skips these verified source signatures even through folder import.

Scout performs episode identification and final naming. Automatic sidecar import into Scout is not implemented.

## Build and test

Requires Windows, the .NET 9 SDK to build, and the .NET 9 Windows Desktop Runtime to run. MakeMKV is a separate installed tool. Ripley does not require HandBrake.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Scripts run from the repository root. The executable is published to `artifacts\app\Ripley.exe`; intermediate files, test renders and backups are under the Git-ignored `artifacts` directory.

Tests use temporary state and a fake process; they do not access a physical disc or live app data. Coverage includes ripping success, cancellation, partial failure, collision-safe publication, history recovery, legacy queue preservation, inbox delivery/retry, acknowledgement while Ripley is closed, and completion reconciliation. Contract tests simulate recipient responses through SQLite without loading Tucker.

Source is under `src\Ripley`; tests and fake tools are under `tests`. The mascot source artwork and Windows icons are in `src\Ripley\Assets`.

## Scout shared storage compatibility

Ripley's TVDB lookup reads the configured Doggo database directly using its own adapter. Scout must save credentials in that database. Rotated keys and PINs take effect on the next lookup; removed, ambiguous or unavailable credentials fail without searching private Scout files. Keys remain DPAPI-protected and are never copied into Ripley settings or logs.

Updated Ripley holds a shared lease on Doggo storage while open. Database cutover requires an exclusive lease with the apps closed. Tests use isolated databases and synthetic protected credentials; builds and packages do not install apps or migrate live data.
## Installers and CI

`eng/test.ps1` validates this repository independently. `eng/package.ps1` builds an unsigned, self-contained Inno Setup installer in a new output directory. `eng/test-installer.ps1` checks isolated installation, upgrades, locked-file refusal, the installed app UI and uninstall with data preservation. GitHub Actions runs these checks per repository and creates draft releases for version tags without requiring signing credentials. See [Windows installers and releases](docs/releases.md).
