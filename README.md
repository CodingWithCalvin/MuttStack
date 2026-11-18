# Scout

A Windows desktop app for identifying TV episodes from video dialogue, reviewing the evidence, and organizing files into episode names and season folders.

## Features

- One saved process per Scout, tied to a source video folder. Pause, resume, review issues, or identify newly added files without repeating successful work.
- Show discovery from folder names, filenames and embedded metadata. Confirm catalog hints directly, or ask AI to verify sample dialogue.
- TVDB, TMDB and TVmaze title/ID lookup and episode-guide downloads.
- Confirm the series without loading a model; choose any subset of episodes for dialogue identification.
- Match dialogue against SubDL subtitle references first, with AI fallback for unresolved files. Reuse saved references, dialogue, fingerprints and exact-input comparisons; load a managed model only when AI is needed.
- Choose one episode, one season, the full show or a custom multi-selection.
- Verify two consecutive episodes in one recording, or one episode split across two recordings, with distinct combined/split previews and duplicate handling.
- Subtitle evidence with canonical provider IDs and numbering, or AI dialogue matching with a second verification pass and original evidence.
- Duplicate copies grouped together; competing recordings held for automated issue review or an explicit human assignment.
- Local model endpoints or a managed local runtime, with optional cloud providers.
- Settings for model downloads, usage, and API keys, with encrypted Windows credentials and explicit opt-in for cloud dialogue and downloads.
- Cached rename/move previews, selective moves, and separate handling for duplicates and non-episode features. Saved analysis follows moved files.
- Preserve correct names by default, or use one relative-path template with dot-chained formatting, syntax highlighting, and completion. Global naming settings apply to every Scout.
- Preview reasons distinguish formatting-only changes, folder moves, episode corrections, and unchanged files.
- Searchable episode results and a draggable divider to give dialogue evidence more room.
- SQLite storage for settings, evidence, results, and history, plus Scout deletion with undo and Windows taskbar shortcuts.

## Screenshots

### Episode results and dialogue evidence

Review matched episode numbers and titles alongside the dialogue that supports each match.

![Scout episode results and dialogue evidence](docs/screenshots/results.png)

### Scout overview and setup

Keep the source folder, metadata provider, series selection, and activity together in one Scout.

![Scout overview and setup](docs/screenshots/overview.png)

## Current limits

Identification and preview generation do not change source media. The move preview can explicitly apply checked rename/move operations; files are never overwritten or deleted. Audio transcription is not implemented. Embedded text subtitles are supported; saved dialogue can be supplied as an advanced override. Episode guides can be downloaded from a selected metadata provider or imported under Settings > Usage and tools > Advanced run data.

FFmpeg/FFprobe are supplied separately. Managed local runs automatically install the pinned llama.cpp b11908 Windows x64 runtime for the Qwen3.6 and Gemma profiles when downloads are enabled. Scout verifies the archive SHA256, installs the executable with its libraries, and verifies installed files before reuse. Zero GPU layers uses CPU; other values select Vulkan and require a compatible graphics driver. Settings also offers a runtime-only download. Scout always manages the llama-server executable when launching a model. Custom GGUF models are not automatically guaranteed compatible. Downloads stay in Settings and can be cancelled without adding events to a Scout run. Experimental model downloads require opt-in; no model is universally approved. Cloud requests require separately configured credentials and can incur provider charges. Saved-evidence benchmarks compare model decisions against inherited references; neither those references nor synthetic fixture tests establish independent real-world accuracy.

## Show discovery and episode identification

In **Overview and setup**, choose the source folder first, then the metadata provider:

- **TheTVDB** (default): API key, plus subscriber PIN when required by the key type. Uses TVDB series IDs and the provider's default episode order.
- **TMDB**: API Read Access Token. Uses TMDB TV series IDs and season/episode numbering.
- **TVmaze**: no API key. Uses TVmaze show IDs and numbering.

Configure credentials under **Settings > Show discovery**. Credentials are encrypted for your Windows account. Provider IDs are separate namespaces; a TMDB or TVmaze ID is never treated as a TVDB ID.

**Identify series** checks folder names, filename prefixes, provider ID tags, and embedded series tags against the selected catalog. Review the suggestions and choose **Use this show** to confirm without loading a model, or choose AI assistance. Scout automatically samples up to two videos for discovery; these samples do not limit the episodes you can identify later. AI checks compact original-dialogue excerpts against catalog candidates and expands inconclusive evidence when needed. IDs always come from the provider. Remakes, mixed shows, or missing evidence can remain unresolved.

**Identify episodes with AI** opens the scope picker for one episode, a season/folder, the full show, or a custom selection. Previous identification selections are preselected. Cancel closes the picker without starting processing. Scout downloads a missing guide for the selected show, saves its own snapshot, and matches each selected recording against it. Results, dialogue, checkpoints, usage, and exact-input model caches stay with the Scout.

Missing episode descriptions are retrieved before model processing, including when resuming a saved guide. Scout checks the selected provider's episode details first, then can supplement TVDB or TMDB guides with TVmaze descriptions through verified external series IDs. Episode titles and season/episode positions must agree; omitted multipart suffixes require a complete, aligned group. Supplemental descriptions retain their source links. The selected provider still controls every series ID, episode ID, title, number and rename path. Existing descriptions are preserved, recovered descriptions are cached with the guide, and unresolved gaps produce an explicit hold when a model selects that episode.

To start an independent analysis, choose **New Scout**, including when reusing the same source folder. To continue an existing analysis, use **Resume Scout**, **Review issues**, or **Reanalyze Scout**. Provider numbering can differ; each Scout retains the identity and guide used for its results. Saved guides can be imported under **Settings > Usage and tools > Advanced run data** and must match the selected provider and series.

**Force stop** cancels the current identification or review immediately, including after a pause has been queued. Completed results and available evidence stay saved; the interrupted file is held until **Resume Scout** retries it. Scout confirms when cancellation and saving have finished. **Pause Scout** finishes the current file before stopping.

Provider documentation and attribution: [TheTVDB v4](https://thetvdb.github.io/v4-api/), [TMDB](https://developer.themoviedb.org/docs/getting-started), [TVmaze API](https://www.tvmaze.com/api). This product uses the TMDB API but is not endorsed or certified by TMDB. TVmaze data is provided under [CC BY-SA](https://creativecommons.org/licenses/by-sa/4.0/). Provider access and distribution terms apply; no shared API keys are bundled.

## Naming preferences

Open **Settings > Naming** to choose naming preferences for all Scouts. Preserve mode accepts equivalent spacing, punctuation, and season-folder numbering when the episode number and title agree with the match. Unrecognized names receive an episode-naming proposal; contradictory episode information is marked as a correction.

Use one relative path template for both folders and filename. It includes the series, episode identity, and title, with optional provider/ID tags and transformations applied only where requested. Scout adds the source extension. See [Naming path language](#naming-path-language) for an example and supported functions.

Saving naming preferences updates the preview without running the model. Cosmetic changes remain distinct from episode corrections; files change only when their moves are explicitly selected and applied.

If the selected source is already a season folder, Scout uses it as that season's destination instead of appending the season again. Template subfolders after the season component still apply. The selected folder remains the move boundary: matches belonging to another season stay held and require a Scout rooted at the show folder for moves between seasons.

## Combined and split recordings

Dialogue matching can assign two consecutive catalog episodes from the same season to one file. The two installments may be separate stories or parts of one continuous story. Both require complete episode coverage, distinct supporting blocks in playback order, and independent guide verification. An ongoing story or cliffhanger does not by itself mean the recording is incomplete. Preview names use `S01E01-E02` and both titles. This is one combined assignment, not two competing guesses.

For a split episode, both files must be selected in the same identification run. Each must have dialogue evidence identifying it as a partial recording of the same episode. After collapsing exact copies, Scout compares the two full dialogue sources twice independently. Both checks must establish complementary story continuity and agree on order. Preview names include `(Part 1 of 2)` and `(Part 2 of 2)`. Files are retained separately; Scout does not concatenate or split media; each part can be moved or renamed separately from the move preview. Identical copies of either part remain duplicates. Overlapping dialogue, competing complete recordings, a missing half, three or more distinct contenders, or inconclusive verification remain unresolved.

Combined and split assignments appear separately in results, evidence and move previews. Combined files count both episodes; split files count one episode. Filename-only matching holds multi-episode ranges and part labels for dialogue review. Duration and filenames never prove multipart content. Current support is limited to two consecutive episodes per combined file and two recordings per split episode; larger or nonconsecutive combinations remain for review. Rerun identification to evaluate old results with the new matcher; old single-episode caches are not reused as multipart evidence. Fixture tests validate the workflow and safety checks; real-media model accuracy still needs benchmarking.

## Subtitle matching with AI fallback

In **Settings > Subtitles**, save a SubDL key and leave **Match subtitle dialogue before using AI** enabled. Subtitle searches send only series identifiers; comparisons run locally. AI fallback uses the existing Connection settings and cloud-dialogue consent. If the key is absent, the service is unavailable, or a match lacks sufficient support, Scout uses saved references where available and sends only unresolved files to the configured AI path. The managed model is downloaded/launched only when an actual AI request is needed.

Subtitle assignments retain the selected metadata provider's series/episode IDs, titles and numbering. Upload episode numbers alone cannot assign a provider episode: references require a verified series identity and unique title mapping, or distinctive dialogue linking them to titled references. Ordered Part 1/Part 2 references can support one provider episode; a complete combined reference can support two consecutive provider episodes. Partial recordings, promos and ambiguous mappings fall back to AI. Duplicate and collection-conflict checks still apply; competing recordings can trigger the existing automated AI review.

References and comparison results are cached in Scout's database, scoped to provider/series, guide, dialogue and matching revision. Completed reference retrieval is reused for seven days; saved references remain usable offline. Changes to the guide, dialogue or reference set invalidate exact-input comparisons. Match review labels subtitle evidence separately from AI assessments and shows reference provenance, provider mapping, text overlap and source dialogue. Overlap percentages are not confidence probabilities, and uploaded subtitle labels can still be wrong.

Installing a build or changing these settings does not rewrite saved results. Resume retains successful unchanged assignments; use **Review issues** or **Reanalyze** when you explicitly want existing files evaluated again. Readable embedded text subtitles are still required by this build; audio transcription is not yet available.

## Resume an interrupted Scout

Select the saved Scout and choose **Resume Scout**. Scout keeps its original file selection and episode guide, reuses successful model results and fingerprints for unchanged files, and retries failed, changed, or unfinished files with the current model settings. Successful files are checked against saved size, modification time, sampled digest, and guide identity before reuse. Missing files are reported individually without discarding other completed results. Existing unresolved model judgments remain available for review; restart does not force them into assignments.

Reports are checkpointed after each file. An interrupted model request must be repeated; completed results remain saved. Resuming does not rename or move media; changed results invalidate the cached move preview.

Each sidebar entry is one Scout with one saved process. Multiple Scouts can use the same source folder; choose **New Scout** to start another. **Resume Scout** continues the selected Scout using its saved selection and completed results. Existing saved runs appear as independent Scouts; their files and history are retained.

During dialogue identification, **Pause Scout** queues a pause immediately and shows a persistent acknowledgement. The current file finishes model verification and fingerprinting and is saved before Scout stops; it does not cancel the active model request. During split verification it finishes the current verification pair. **Resume Scout** continues from these saved checkpoints. Other activities, such as downloads or show discovery, retain their cancel action.

## Local SQLite storage

The Windows app stores settings, Windows-encrypted API credentials, Scout history, guide snapshots, dialogue, per-file results, fingerprints, model caches, usage, logs, and previews in `%LOCALAPPDATA%\Scout\scout.db`. SQLite may maintain its own journal/WAL files alongside it. Model downloads and managed runtime binaries remain in the configured model folder. Source videos are never stored in the database.

Fresh installations start directly with SQLite. Existing database contents remain in place across upgrades. Legacy loose files are not scanned or automatically imported; any retained files are inactive backups. Explicit report/guide import remains available. One app instance is allowed at a time. Browser previews are exported to a temporary folder only when explicitly opened. Developer command-line workflows can still read or explicitly export standalone reports.

The versioned schema uses document records for settings/history/guides/caches and a separate indexed `file_results` table for each report's file checkpoints. Credentials remain DPAPI-encrypted blobs. Reads and checkpoint replacements are transactional, with WAL and full synchronous writes. A newer unsupported schema is rejected rather than downgraded.

Scout also supports a staged Doggo database backend with its own `scout_` tables. It is **opt-in only**: normal startup keeps the current Scout database. The isolated rehearsal command uses SQLite online backups and verifies saved results, encrypted credential bytes, and unrelated app tables. See [Doggo database support](docs/doggo-database.md) before testing a copy; live cutover and Tucker-to-Scout handoffs remain separate work.

Right-click a Scout in the sidebar history to **Archive** or **Delete** it. **Show archived** reveals archived Scouts with **Restore from archive** in their context menu. Deletion retains saved data and supports Undo delete; archiving and deletion never change videos. Active Scouts cannot be archived or deleted.

For context overflows, Scout first removes the redundant schema explanation. If the full guide still does not fit, it searches every guide entry in smaller partitions while preserving the entire dialogue in each request, then verifies the retrieved candidates together in two independent passes. Batch suggestions alone cannot become assignments. If the full dialogue or final candidate set still cannot fit, the file stays held with a context-size recommendation that includes the output allowance. No dialogue is truncated, no runtime is restarted automatically, and no local-model settings are changed. Partition retrieval can miss candidates; structural validation and agreement are not independent accuracy guarantees.

## Automated conflict review

After identification and split verification, Scout re-evaluates competing episode claims using each recording's original dialogue and the full episode guide. Two independent full-guide decisions must agree before replacing a suggestion. Filenames, earlier suggestions, and an assumption that one claimant must win are not model evidence. The collection resolver runs again afterward; duplicate claims are still held rather than forced into unique slots. Original suggestions remain in the audit data. Review progress is checkpointed, and errors or paused conflict reviews cannot become accepted assignments accidentally. The match-review panel lists the competing recordings.

**Reanalyze Scout** keeps the same file selection and saved guide, reuses dialogue and primary video/audio fingerprints for unchanged files, and reruns model analysis and independent verification with the current connection settings. It bypasses prior analysis caches and reevaluates combined, split, and conflicting assignments. Previous results are retained internally in SQLite; pending files are held until their fresh analysis completes. Pause and then Resume Scout continues this new analysis without repeating its successful files. Media files are never changed by analysis.

**Review issues** automatically selects unresolved, failed, invalid, or changed recordings and their competing recordings or copies. It reuses unchanged dialogue and fingerprints, runs two independent full-guide checks with the current model, and reevaluates split and duplicate/conflict assignments. Unrelated successful results are preserved. Newly competing recordings are included in the final conflict check. Evidence shows why each file was included; inconclusive results stay held. Pause and Resume Scout retain the issue-review scope. No manual corrections or media changes are made.

If claims still conflict, Review issues checks each disputed episode separately against its own guide and the recording's original dialogue. A combined suggestion can lose an unsupported second episode without assigning an alternative by elimination. These checks are checkpointed individually and reused on resume. Completed full-guide checks are reused during collection reconciliation. Model agreement remains a suggestion, not a guarantee; ties and failed checks stay held.

New model requests classify each candidate's evidence as a complete episode, incomplete episode, preview/promo, recap, or unknown. Completeness concerns the catalog installment, not whether its wider story has ended. Combined files can contain two separate stories or a continuous two-part story; both complete installments must have distinct, ordered guide evidence. Incomplete recordings, promos and recaps cannot supply a second complete episode. Scout rejects incompatible combinations without asking the model to substitute a neighboring title. Combined proposals, including those rejected for numbering or overlapping evidence, receive two focused audits of the original descriptions with title/number fields and previous reasoning omitted. The audit retains explicit catalog part relationships without revealing episode identities; part labels alone never establish a match. After checking each candidate, the model makes one joint recording-level decision: one complete episode, continuous parts, separate complete episodes, an incomplete episode, or unresolved. Individual candidate audits cannot each claim the whole recording. The shared decision must agree with the original guide evidence and playback order, and both independent audits must agree. Guide sentence parsing preserves abbreviations and every original clause. Every guide sentence must be checked, supporting quotes must align to consecutive original words, and a reduced single-episode assignment must account for the recording as a whole. Citation alignment allows subtitle punctuation and an immediate block continuation, preserves the raw model response, and displays the original source span. Unidentified additional installments, disagreement about episode coverage or the relationship between parts, and invalid quotes remain held. For explicit consecutive catalog parts, a shared handoff block is permitted only with two distinct grounded evidence blocks on each side; interleaved coverage remains held. An unresolved related part cannot be silently dropped in favor of the ending. Original proposals and audits are retained. Structural response errors in the initial search can still receive one bounded retry; grounded audits do not repair their output. These safeguards verify provenance and consistency, not the independent truth of the model's interpretation. Missing guide events can cause conservative holds.

Missing sources retain their evidence and history but stop reserving episode assignments after Scout verifies that the source folder is available and cannot reconnect the recording. An unavailable drive does not count as a missing recording. Different encodings can share one assignment only when their complete normalized dialogue is identical, sufficiently substantial, and their durations agree; similar summaries or shared scenes do not establish copies.

In **Results > Match review**, **Force assignment** lets you choose one complete episode from the saved guide and optionally record a reason. The dialog lists affected competing recordings before you confirm. This explicit human decision takes precedence; competing recordings remain held and the model's suggestions are retained separately. Results are labeled **Human override**. **Undo override** restores the previous decision or automatic conflict handling without undoing file moves. Overrides persist through resume and reanalysis while the recording, dialogue and series identity remain unchanged. A changed source, occupied destination, or interrupted move still requires the normal safety checks. Generate a new move preview after changing an assignment; forcing an assignment does not move any files.

Right-click Scout’s Windows taskbar icon for **Ready for review** and **Recent Scouts**. Selecting an entry opens that Scout’s results in the existing window, or launches Scout when closed. During processing, the requested Scout opens after the current activity finishes or pauses. Windows taskbar history settings control whether custom Jump List categories are displayed.

## Naming path language

The template editor highlights fields, functions, strings, and character categories. Type an opening brace for field suggestions, a dot for functions, or press **Ctrl+Space** at the cursor. **Tab/Enter** inserts the selected suggestion; **Esc** dismisses it. Suggestions include examples and descriptions, and validation plus filename examples update as you edit. Editing is local and makes no model requests.

Settings uses one relative path template for folders and filename; Scout adds the source extension. Literal folder spaces are independent of filename transformations. For example:

~~~text
Season {season:00}\{series.replaceWhitespace(".")}.{code}.{titles.remove(punctuation).replaceWhitespace(".").join(".and.")}{partSuffix.remove(punctuation).replaceWhitespace(".").prefix(".")}
~~~

Tokens include series, code, title, titles, season/episode (optionally :00), provider, id, part, parts and partSuffix. Functions run left to right. Titles are a list: transformations before join apply to each title, and transformations afterward apply to the joined text.

- remove(punctuation), remove(symbols), remove(diacritics), remove(whitespace), remove(quotes) use defined character-category selectors.
- remove("text") removes literal text only, even when it spells a selector name.
- replaceWhitespace("."), replacePunctuation(" "), replace("&", "and"), lowercase(), uppercase(), trim(), prefix("text"), and suffix("text") provide explicit formatting.
- join(" and ") joins combined titles. Episode codes retain their range.
- Combined entries with the same title and part numbers 1/2 use their shared story title: `Encounter at Farpoint (1)` + `(2)` becomes `S01E01-E02 - Encounter at Farpoint`. Different titles, such as `Space Chicken` and `Steak Night`, retain both names. Provider titles and episode IDs remain unchanged.
- partSuffix is empty for complete files or contains (Part 1 of 2), including a leading space. A split suffix is added automatically when no part token is present.

Functions use dot chaining. Arguments are readable double-quoted strings; quotes and backslashes inside them are escaped. Older pipe templates display as equivalent dot chains when opened in Settings; unknown functions/selectors are rejected. Braces escape as {{ and }}. Templates must stay relative, cannot traverse parent folders, and must include a season folder token, episode code and title. Windows-invalid filename characters are sanitized. Existing naming preferences are converted when opened in Settings. **Preserve correct names** remains the default; choose **Standardize all names** to apply purely cosmetic changes.

## Retained evidence and added videos

**Locate moved files** lets you select the current source folder. Scout also checks missing saved paths when resuming, reanalyzing, reviewing issues or identifying additions. Size and sampled hashes narrow candidates, then complete primary video/audio fingerprints verify the recording before reconnecting its saved analysis. Original paths and verified changes remain in the evidence history. Ambiguous copies remain held; no filename-based identity guessing or video changes occur.

**Identify new files** scans the selected Scout's source folder for added videos, identifies only those additions, and checks their assignments against saved results. New conflicts include their competing recordings in automated review; unrelated saved results (including older failures) stay unchanged. The incremental scope is checkpointed, so **Pause Scout** and **Resume Scout** continue that batch without repeating successes. This is an explicit scan, not a background folder watcher. Use Review issues or Resume Scout to retry older failures.

Move previews are cached in SQLite and restored across launches. Matching results, guide, naming preferences, series identity, source location, source file metadata and destination occupancy determine validity. **Regenerate preview** explicitly recalculates. Older previews without a cache signature need one regeneration. **Open full preview** launches Edge, Chrome or Firefox directly, preferring the recognized default HTTPS browser rather than the HTML file association.

The missing-episode list follows the saved identification scope. A season selection checks only that season; a full-show selection checks the show (or just the source season when rooted there). Individual files, custom selections and arbitrary folders do not imply that other catalog episodes are missing. Older Scouts without a saved scope only receive a season coverage check when their source folder identifies the season. Matching still considers the full provider guide so misplaced files can be identified correctly.

The Windows template editor uses [AvalonEdit](https://github.com/icsharpcode/AvalonEdit), distributed under the MIT license. Its notice is included in THIRD-PARTY-NOTICES.txt.

## Apply selected moves

In **Move preview**, check individual files or choose **Select pending**, then click **Move / rename selected**. Only checked, unblocked rows are eligible. The status column distinguishes **Pending**, **Completed**, **Held**, **Unchanged**, and failed attempts that can be retried. The list updates after each file, and completed history stays available after reopening or regenerating the preview.

Scout revalidates the preview, source fingerprints and destination occupancy before applying changes inside the source folder. Same-volume renames use Windows file identity, saved size/time and small fingerprint samples (up to 192 KiB per sample check), with a read handle blocking concurrent writers. They do not reread entire videos or silently fall back to cross-volume copies, and never overwrite an existing destination. Selected dependencies are processed in order; unselected occupants and rename cycles stay held. Junctions/symbolic links are not traversed, and empty source folders are retained. Duplicate copies move into their own destinations; they are not deleted.

Each move's intent is saved in SQLite before the filesystem operation. Completion updates the recording path, path history and original analysis together. **Stop after current move** stops the batch while retaining completed work. After an interruption, **Regenerate preview** reconciles pending moves against destination identity and saved samples (a full saved-fingerprint check is used only for legacy journals or recovery on storage without file IDs); it does not automatically retry a filesystem move. Uncertain locations remain held. No model requests are needed to apply moves.

## Development

Scout is built with C# and WPF on .NET 11, using the prerelease SDK pinned in `global.json`.

### Build and run

On Windows, install the SDK selected by `global.json`, then run:

```powershell
./build.ps1
./artifacts/win-x64/Scout.exe
```

Pass `-DotnetPath C:\path\to\dotnet.exe` to use a particular SDK installation. The build runs the core and desktop startup checks and publishes a self-contained x64 app. End users do not need an installed .NET runtime.

### Package

```powershell
./package.ps1
```

Packaging produces a ZIP and SHA-256 file in `artifacts/releases`. Use `-SkipBuild` only after a successful build.

### Repository layout and tests

- `Scout.Core`: media pipeline, matcher, model transport, collection resolution, preview planner, settings and workspace persistence.
- `Scout.Desktop`: WPF application and Windows integration.
- `Scout.Tests`: executable regression suite with synthetic fixtures and mocked HTTP.
- `Scout.Desktop.Tests`: Windows startup and history-menu checks using temporary data, without opening the user's database or loading models.
- `Scout.Core/Assets`: matcher prompts and JSON schemas.
- `Scout.Desktop/Assets`: mascot source PNG, application PNG and multi-resolution ICO.
- `scripts/Generate-Icon.ps1`: exports the dog mascot source artwork to PNG and multi-size Windows ICO without external graphics tools.

Run tests with:

```powershell
dotnet run --project Scout.Tests -c Release
dotnet run --project Scout.Desktop.Tests -c Release
# Optional live, key-free provider smoke test:
dotnet run --project Scout.Tests -c Release -- --metadata-smoke
```

### Tucker inbox (isolated support)

With prepared Doggo storage, **Inbox** shows compressed outputs sent by Tucker and a badge for waiting batches. Accepting saves draft Scouts grouped by series, with exact file selections; it does not start processing. Acceptance persists while Tucker is closed. Normal startup uses the standalone Scout database until the [cutover tool](tools/Scout.DatabaseCutover/README.md) saves a persistent Doggo selection. Future launches and taskbar entries honor that choice. The tool has been tested on isolated copies; live migration and installation remain deferred. See [Doggo storage](docs/doggo-database.md) and [the isolated handoff rehearsal](tools/Scout.HandoffRehearsal/README.md). Building this support requires the matching sibling `Tucker` checkout containing `src/Doggo.Handoffs`.

### Local data

Scout stores preferences, history, and processing results in `%LOCALAPPDATA%/Scout/scout.db`. Set `SCOUT_WORKSPACES` to override the history location. `SCOUT_SETTINGS` overrides the desktop preferences directory for isolated testing.

Guides, results, usage, previews and events are stored under each Scout in SQLite, outside the source video folder. New Scouts retain their own guide snapshot. Do not commit media, transcripts, user reports, model weights, keys, or machine-specific settings.

### Automated recheck of saved model errors

For local inference, `dotnet run --project tools/Scout.Recheck -c Release -- "<run directory>" "<settings directory>"` revalidates saved assignments, reruns files with recorded model errors using the configured local model, and regenerates that run's report and move preview. It verifies current source files and reuses unchanged dialogue. It does not rename or move videos or manually assign episodes. Close the run in the UI while rechecking to avoid concurrent edits. An optional third argument writes the merged report and preview to a separate output directory, leaving the saved run untouched until those results are copied back.

Guide cards sent to the matcher include catalog season and episode numbers. Invalid model responses receive at most one retry per inference stage, within the configured request budget, with the original dialogue and a validation explanation. Failed attempts remain in the saved diagnostic evidence; unsupported results remain held. Collection validation checks selected assignments without treating rejected comparison candidates as additional assignments.

### Isolated model benchmarks

Episode identification starts with deterministic retrieval across the whole transcript and each temporal third. Selected original passages and a catalog shortlist produce compact candidate IDs. Inconclusive retrieval expands automatically to the full dialogue and catalog; context recovery partitions the catalog without truncating dialogue. Retrieval never creates an accepted assignment on its own. Production verification audits complete original dialogue against every proposed guide statement. Combined episodes and conflict review retain two agreeing full-dialogue audits. Excerpt-only verification remains an opt-in benchmark experiment: it requested full dialogue in every measured difficult case, adding a redundant call. The evidence panel reports the actual audit scope, including for older saved results. Compact responses cite stable original span IDs; Scout reconstructs verbatim excerpts and review labels without changing model verdicts. Coverage, chronology, promo/recap, source-grounding and duplicate/conflict checks remain enforced. Missing candidates and unsupported descriptions may still cause holds, and this workflow needs evaluation beyond the saved development cases.

The [compact evidence benchmark](tools/Scout.RetrievalBenchmark/README.md) evaluates the production workflow on unchanged saved dialogue with CPU-only inference and thinking disabled. It writes only isolated benchmark artifacts.

The `tools/` projects contain the automated model comparisons and diagnostic runners. See the [model comparison](tools/Scout.Benchmark/README.md), [decision-model evaluation](tools/Scout.JudgeBenchmark/README.md), [fast workflow](tools/Scout.FastBenchmark/README.md), and [compact verification benchmark](tools/Scout.VerificationBenchmark/README.md) instructions. Personal fixtures, transcripts, generated reports, databases, model weights, and Python caches are excluded from Git. Some diagnostic scripts use local experiment paths; review their inputs before running them.

Prompt processing and answer generation have separate CPU thread settings. Prompt threads set to `0` retain the generation thread count; explicit values apply at the next managed model launch. Tune with measurements rather than assuming more threads are faster. The search and audit requests share a stable system/dialogue prefix, followed by stage-specific catalog and instructions, so llama.cpp can reuse matching dialogue computation. Each call still supplies its own inputs and no previous model answers.

Scout is licensed under the [MIT License](LICENSE).
