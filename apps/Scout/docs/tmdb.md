# TMDB catalogs and multipart recordings

New settings default to TMDB. Saved provider choices, existing Scouts and their catalogs stay unchanged. Configure the **API Read Access Token** under **Settings > Show discovery**. It is encrypted for the current Windows account in the selected database. Ripley can read that credential from the same Doggo database; no credential is bundled or copied into either repository.

Scout requests English TMDB series and season details. A `{tvdb-...}` folder can suggest a TMDB series through a forward external-ID lookup and a confirming reverse lookup. This identifies the series only: TMDB's episode IDs, titles and season numbering still control matching and output. To process an existing TVDB library against TMDB, create a new Scout, select TMDB and confirm the series. An already processed Scout cannot switch providers in place. There is no automatic retagging or conversion of existing files.

One recording can cover one complete episode or two to four distinct, consecutive episodes from one season. Matching, independent model verification, collection resolution, manual assignment and naming all preserve the complete range. For example, three TMDB installments produce `S04E11-E13`; numbered titles of the same story collapse to one shared title. Every selected installment needs complete, ordered coverage. A disc play-all is still handled by the existing compilation checks. This does not split a recording into separate files, and ranges longer than four remain held.

Subtitle upload numbers alone are not provider identities. References need a unique catalog title, distinctive dialogue linking to a verified reference, or a user-confirmed reference identity. A recording filename can also provide a strictly corroborated, recording-only title hint, as described below.

An exact whole-story title can map a single release episode to the selected catalog's consecutive numbered parts, even when the release uses another season or episode number. Scout still requires strong dialogue extending through both the recording and reference. Equivalent complete subtitle versions support the same assignment; a partial version cannot inherit complete coverage. Explicit part labels stay scoped to their part, and ambiguous titles or malformed ranges stay held. When the provider leaves its first installment unnumbered (for example, `Story` followed by `Story - Part 2`), a whole-story assignment requires an explicit complete range; the bare first-installment title cannot claim both halves.

First-person title openings such as `iWill Date Freddie` and `iWant to Date Freddie` can also link when the remaining distinctive wording matches exactly and only one catalog story qualifies. Names, actions and negation remain significant. Strong, complete dialogue correspondence is still required; short titles and competing catalog interpretations are not accepted by this rule.

Move previews show duration and relative-size clues for possible combined recordings when at least five identified single episodes are available in the same folder. The comparison uses a median to reduce outlier effects. Duration takes priority; size alone is a weak clue because compression varies. Neither changes an assignment or proves how many episodes the selected provider lists. Unresolved subtitle comparisons show their actual matching or catalog-mapping reason, including when AI fallback is disabled.

To review a strong full-length match with a single unnamed reference:

1. Open **Force assignment** for the recording and select the full episode range with Ctrl/Shift.
2. After checking its actual identity and full coverage, select **I verified the matched subtitle reference covers this full episode selection**. The checkbox is off by default and unavailable without sufficiently strong, complete subtitle evidence.
3. Recompare saved dialogue. The review is scoped to that Scout, provider, series and exact reference text. It cannot carry a TVDB numbering decision into TMDB. Conflicting reviews stay held until the obsolete assignment is undone.

Undo disables the reference mapping and expires matches derived from it. Saved move history is retained; undoing an assignment does not undo completed file moves. Normal destination previews and source/collision checks still apply.

## Offline regression replay

`tools/Scout.TmdbRehearsal` accepts a previously captured, API-verified iCarly audit and a new output directory under `.build/tmdb-rehearsal-*`. It reads only the copied database, saved catalog JSON and saved dialogue; it creates synthetic media placeholders and previews. It never opens recorded media paths or contacts a service. Any reference confirmations performed by the replay are explicitly **simulated test decisions**, not approvals for a live Scout. The private audit and its subtitle text are not repository fixtures.

```powershell
dotnet run --project tools/Scout.TmdbRehearsal -c Release -- C:\isolated-audit C:\scratch\.build\tmdb-rehearsal-example
```

API behavior follows [TMDB application authentication](https://developer.themoviedb.org/docs/authentication-application), [external-ID lookup](https://developer.themoviedb.org/reference/find-by-id) and [season details](https://developer.themoviedb.org/reference/tv-season-details). This product uses the TMDB API but is not endorsed or certified by TMDB.

## Reference validation and short titles

Unique exact titles with two or more letters/numbers, including `iQ` and `iDo`, can establish a provider-title link. Short titles do not receive fuzzy or prefix matching; duplicate catalog titles remain ambiguous. Full, distinctive dialogue and complete coverage are still required.

Downloaded and cached references are validated before comparison, including offline cache reuse. Malformed cues and clearly non-English scripts are excluded. With sufficient independent reference and catalog evidence, a long transcript missing every recurring series name is quarantined as a possible wrong-show upload. This conservative consistency check learns names from the catalog and reference cohort; it contains no show-specific rules. Small samples, duplicate uploads and short extracts cannot establish an outlier. It is a screening check, not proof that every remaining reference is correct.

Progress reports quarantined files separately and distinguishes subtitle file counts from title-linked provider episode coverage. Untitled references are still available for dialogue bridges or explicit user review; their release numbers are never assumed to be TMDB episode IDs. Updated retrieval and comparison revisions prevent stale comparison caches from bypassing these checks. Existing successful run results stay resumable; reanalysis applies the new checks to their saved dialogue.

## Missing references, release titles and duplicate uploads

English retrieval considers numbered individual releases as well as season packs. When a season search leaves gaps, Scout searches the missing episode labels individually. A successful download can fill several gaps and suppress later unnecessary queries. Release titles attached to a specific archive entry can establish its unique provider title; season-pack titles are never applied indiscriminately to every entry. Requests use the series identity verified through the selected metadata provider. Search results explicitly identifying another series are rejected. The season/episode query parameters are retrieval hints, never provider identity proof.

Broken, missing or expired archives are skipped so alternative releases can be tried. Authentication failures and account limits stop retrieval while retaining already saved references. Each refresh permits at most 60 subtitle searches and 48 archive downloads, with the existing archive size limits. Missing coverage stays visible. Incomplete caches are eligible for refresh; older complete-cache policies are refreshed too. Numbered-file coverage only controls retrieval, not automatic episode assignment. See the official [SubDL API documentation](https://subdl.com/api-doc).

Near-identical full transcripts are treated as equivalent versions, even if their upload numbers differ. Repeated uploads count as one story when identifying common show boilerplate, so they cannot erase the episode dialogue from comparison. This requires at least 120 distinctive shared sequences, 97% coverage in both directions and matching dialogue extent. Partial recordings and different endings remain separate. Conflicting known titles or reviewed identities remain blocked, and duplicate content under different labels cannot make both reference slots appear complete.

A unique exact title already present in a recording filename can help map an unnamed reference only when the full dialogue comparison passes, distinctive title terms recur together through the recording and appear in the reference, and distinctive synopsis terms support that story over the rest of the catalog. An opening announcement alone is insufficient. Ambiguous, generic, creative-but-unspoken or conflicting titles stay held. Explicit partial recordings cannot use this path. This hint applies only to the current recording; it never becomes a reusable identity for other references or Scouts. Evidence review shows the filename and corroborating title/synopsis terms. Neither filenames nor synopsis word overlap alone establish identity.

Comparison caches include the source filename and matching revision. Existing successful results remain resumable. Reanalysis applies these changes to saved dialogue and refreshes incomplete reference coverage without requiring media moves.

## Multiple active Scouts

Create or reopen a Scout and start it while another is working. The sidebar remains available, shows Running or Queued, and restores each Scout's own progress and model activity. Two workers can run concurrently; more Scouts wait for a slot. Pause and Force stop affect the selected Scout, including one waiting for a worker or model. Closing requires all active and queued Scouts to stop. After an unexpected exit, both Running and Queued Scouts recover as Interrupted and can be resumed.

Speech is completed before AI fallback begins. Speech calls share a single model gate with AI; a run using AI retains it until its model is unloaded, avoiding competing managed servers and GPU allocations. Other Scouts can still perform subtitle comparisons. Global model settings stay locked while workers are active. Series discovery participates in the same resource coordination.

Active and queued Scouts reserve their source roots. Another Scout using the same, parent or child root must wait. Moves targeting an active source root are blocked, including moves initiated from another saved Scout. Each Scout keeps its own catalog, report, event history and cancellation state. Shared subtitle-cache refreshes are serialized per series. All coordination is internal to Scout; no code is shared with the other Doggo repositories.
