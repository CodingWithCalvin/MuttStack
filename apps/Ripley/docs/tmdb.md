# TMDB series selection

The series search selector defaults to **TMDB**; **TVDB** remains available. The choice persists across launches. Search results display provider, ID and year so similarly named series can be distinguished.

Save a TMDB **API Read Access Token** in Scout's **Settings > Show discovery**, using the same Mutt Stack database configured in Ripley. Ripley reads the encrypted credential for the current Windows account without modifying Scout's settings. TVDB credentials and PINs stay separate. Search requests send the bearer token only to TMDB's official API; tokens are not part of URLs or error messages.

New default raw folders use:

```text
{series} ({year}) {{{provider}-{id}}}\{disc_label}
```

`{provider}` and `{id}` (also `{series_id}`) follow the selected series. `{tmdb_id}` and `{tvdb_id}` require that exact provider and fail if another provider is selected. Saved custom formats remain unchanged; replace an explicitly TVDB-only format before ripping a TMDB-tagged series. The existing output preview shows the resolved path before a rip.

The selected provider and series ID travel in each file's metadata and the immutable Tucker inbox payload. Disc season/volume labels remain source hints, not a TVDB-to-TMDB episode conversion. Ripley does not assign episode numbers. Existing runs and media are not migrated or renamed by changing the search provider.

All Ripley implementation and tests live in this repository. Mutt Stack database records are the only app integration point; Scout or Tucker source is not referenced.

See [TMDB application authentication](https://developer.themoviedb.org/docs/authentication-application). This product uses the TMDB API but is not endorsed or certified by TMDB.
