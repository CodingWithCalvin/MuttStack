# Subtitle comparison feasibility

This isolated .NET tool compares saved dialogue with SRT references obtained through SubDL's authenticated API and reconciles supported matches to the configured metadata provider. It does not call a model, read media, or change Scout's results. The matching, title mapping, download safety and text decoding code is shared with `Scout.Core.Subtitles`, which now runs before AI in the desktop identification pipeline. This tool continues to write only isolated evaluation results.

```powershell
dotnet build tools/Scout.SubtitleBenchmark -c Release
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --self-test
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --api fixture.json empty-output-directory
```

Save the key in **Scout Settings > Subtitles > Save SubDL key**. Scout uses its existing Windows-account DPAPI protection inside the database and displays a persistent configured indicator. The runner opens that database read-only, reads only the credential document, and sends the key in HTTPS headers to fixed SubDL API/download hosts. Keys are never command-line arguments, URL query values, ordinary settings properties, or report fields. A blank settings field retains the saved key; removal affects only SubDL.

If the runner has a different Windows profile or app-data root, append the absolute Scout database path and logical settings-root path to `--api`. Windows must still be able to unlock the credential for its owning account. This does not copy or modify the application database.

The fixture uses the retrieval benchmark's saved `cases`. The API adapter requires a unique exact normalized TV-series name, searches every catalog season, and selects one English pack per season by listed-file count then stable URL. It never chooses references using source filenames, dialogue scores, or expected episode labels. Archives and reference provenance are retained in the isolated database. Downloads, decompression and request duration are bounded, redirects are restricted to SubDL hosts, and SRT entries are read without filesystem extraction. Unicode BOMs and strict UTF-8 are supported, with Windows-1252 fallback for older BOM-less text. Access failures and missing packs are reported rather than interpreted as wrong matches. See the [SubDL API documentation](https://subdl.com/developers).

The older public-page mode remains available with `fixture.json public-pages.json empty-output-directory`. Its page manifest contains `series` and an explicit public SubDL `url`. Access controls are never bypassed. Zero available references produces a blocked report and exit code 2.

Comparison normalizes case, punctuation, speaker labels and SDH markers, then measures shared five-word sequences across all reference episodes for the series. Repeated corpus phrases are excluded. The provisional screen requires 60 shared sequences, 45% coverage on both sides, support in three of five sections on each side, and a 0.15 lead over the next distinct assignment. These values are not calibrated confidence. Input filenames do not affect ranking. The original `--api` screen displays only unverified title candidates; provider mapping uses the additional workflow below.

The isolated SQLite database retains download provenance, reference cues and comparison results. `--render output-directory` regenerates the original API-screen HTML report without networking or changing saved outcomes. `--audit-key output-directory scout-database settings-root` checks artifacts for accidental subtitle/metadata credential disclosure without printing credentials. Self-checks cover six comparison cases, two encoding groups, nineteen provider/multipart cases, and nine HTTP credential/redirect/error/size cases; they do not establish real-world matching accuracy.

## Provider mapping and combined references

```powershell
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --map fixture.json previous-subtitle-output empty-output-directory absolute-scout-database absolute-settings-root
```

The configured provider comes from the saved fixture. SubDL's IMDb identity is cross-checked through TVmaze's external-ID lookup, then the selected provider's live guide supplies the canonical series/episode IDs, titles, and numbering. TVDB, TMDB and TVmaze use the same catalog adapter as Scout. Saved catalog episode IDs must belong to that provider's live guide. TVmaze provides the series cross-reference, not replacement episode assignments. Metadata credentials are read through the same read-only protected credential path; no app schema initialization or Scout result writes occur.

Number-only upload labels never become provider assignments. Unique reference titles can establish metadata anchors. Formatting normalization handles leading ordinals, punctuation and subtitle language/accessibility suffixes. A one-character typo is allowed only for a long multipart title whose explicit complete range also agrees with the provider's consecutive parts. Ambiguous titles stay unmapped. Additional packs are selected automatically by unique provider-title coverage, then stable URL, with at most two selected packs per season; already downloaded selections are reused. No input filenames or inherited expected assignments choose downloads or mappings.

References without titles are linked to titled anchors using distinctive dialogue, the same coverage requirements, and a margin over competing episode identities. Conflicting anchors stay unresolved. Equivalent editions of one mapped episode do not compete against themselves. An inverted phrase index skips comparisons that cannot reach the existing 60-shared-sequence minimum without changing the acceptance rule.

Explicit Part 1/Part 2 references are paired only within the same archive, series, season and release family with unambiguous counterparts. Each part needs at least 60 distinct shared sequences, 15% of the source text, 45% reference coverage and three of five reference sections. Shared support must occupy ordered, separate spans, each covering at least 15% of the source. Shared sequences appearing in both parts are excluded from these per-part measurements. Combined references can map to one unsplit provider episode or consecutive provider episodes. Partial recordings, duplicate parts, reversed parts and short promos are held. For every assignment, the 10th/90th percentiles of shared dialogue must reach the first/last quarters of both the recording and reference, so a half recording cannot masquerade as a complete episode.

Exact duplicate saved dialogues are grouped; different recordings competing for a provider episode are held as conflicts. Every row retains its mapping provenance, provider IDs, title anchors, candidate rankings, margins and per-part evidence. A separate diagnostic scores only paired references, without full-length candidates, to demonstrate that multipart support works independently; it never overrides the main result. Inherited suggestions are compared only after decisions and conflicts are finalized and are labelled unverified references.

For reproducible offline evaluation:

```powershell
# Fetch metadata/references without producing decisions yet.
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --prepare-map fixture.json previous-subtitle-output empty-prepared-directory absolute-scout-database absolute-settings-root
# Score that preparation once.
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --score-map fixture.json prepared-directory
# Reuse prepared evidence in a new isolated database with no network or credentials.
dotnet tools/Scout.SubtitleBenchmark/bin/Release/net11.0-windows/Scout.SubtitleBenchmark.dll --offline-map fixture.json prepared-directory empty-output-directory
```

Each evaluation records fixture and input-database hashes. Existing outcome directories are not overwritten. Reports show only that evaluation's outcomes. Subtitle labels can still be wrong, reference coverage is incomplete, and these small tests are not independent accuracy validation or permission to rename files automatically.

`--app-replay fixture.json prepared-directory empty-output-directory` exercises the desktop's `SubtitleMatcher`, result adapter and collection resolver against saved references and dialogue, with no reference downloads, model calls, media access or application database writes. It records subtitle assignments and which files would route to AI. Core pipeline tests separately verify actual fallback dispatch, exact-input reuse, source fingerprints, cancellation and rename previews using temporary fixtures and mocked services.
