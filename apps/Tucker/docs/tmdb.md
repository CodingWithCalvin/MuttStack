# Provider-aware compression and Scout handoffs

Tucker preserves the provider and series ID on each source file from Ripley inbox batches or existing per-file metadata. A single Tucker may contain different series and providers. There is no global series fallback or provider setting that overwrites source identities.

New compressed-folder defaults use:

```text
{series} ({year}) {{{provider}-{id}}}
```

`{provider}` and `{id}` (also `{series_id}`) resolve from each source's identity. `{tmdb_id}` and `{tvdb_id}` require the matching provider; they cannot silently write another provider's ID into the wrong tag. Existing custom formats and frozen run output paths remain unchanged. To use new TMDB batches with a saved TVDB-only template, change the template to provider-aware tokens before creating the run.

**Output & settings** previews the actual provider and exact output for a selected source. Without a source, its clearly labeled example uses TMDB, or TVDB when demonstrating a TVDB-only template. Previewing settings never writes media or changes an existing run's frozen paths.

Completed output metadata and **Send to Scout** retain the source's provider and series ID. Scout opens the accepted batch with that provider's catalog; Tucker does not translate DVD volume labels or episode numbers between catalogs. Accepted handoffs remain durable when either app is closed.

Tucker's owned credential reader supports separately encrypted TMDB and TVDB credentials in Doggo. Its owned metadata-search component can use them, but the UI continues to take series identity from selected sources rather than applying a global series selection.

All implementation and tests live in this repository. Other apps communicate through the Mutt Stack database contract; their source code is not referenced. No API keys are bundled.

This product uses the TMDB API but is not endorsed or certified by TMDB. See [TMDB application authentication](https://developer.themoviedb.org/docs/authentication-application).
