# TMDB catalogs and multipart recordings

New settings default to TMDB. Saved provider choices, existing Scouts and their catalogs stay unchanged. Configure the **API Read Access Token** under **Settings > Show discovery**. It is encrypted for the current Windows account in the selected database. Ripley can read that credential from the same Doggo database; no credential is bundled or copied into either repository.

Scout requests English TMDB series and season details. A `{tvdb-...}` folder can suggest a TMDB series through a forward external-ID lookup and a confirming reverse lookup. This identifies the series only: TMDB's episode IDs, titles and season numbering still control matching and output. To process an existing TVDB library against TMDB, create a new Scout, select TMDB and confirm the series. An already processed Scout cannot switch providers in place. There is no automatic retagging or conversion of existing files.

One recording can cover one complete episode or two to four distinct, consecutive episodes from one season. Matching, independent model verification, collection resolution, manual assignment and naming all preserve the complete range. For example, three TMDB installments produce `S04E11-E13`; numbered titles of the same story collapse to one shared title. Every selected installment needs complete, ordered coverage. A disc play-all is still handled by the existing compilation checks. This does not split a recording into separate files, and ranges longer than four remain held.

Subtitle upload numbers alone are not provider identities. References need a unique catalog title, distinctive dialogue linking to a verified reference, or a user-confirmed reference identity. For a strong full-length match with a single unnamed reference:

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
