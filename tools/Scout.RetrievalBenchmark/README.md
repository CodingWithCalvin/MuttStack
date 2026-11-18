# Compact evidence benchmark

Build `Scout.RetrievalBenchmark.csproj` in Release. Prepare an isolated fixture from the saved difficult-case fixture and Farpoint fixture:

```powershell
dotnet Scout.RetrievalBenchmark.dll --prepare difficult-fixture.json farpoint-fixture.json isolated-fixture.json
dotnet Scout.RetrievalBenchmark.dll isolated-fixture.json empty-results-directory
dotnet Scout.RetrievalBenchmark.dll --finalize results-directory
```

The production matcher runs fresh candidate selection and full-dialogue evidence audits. Expected answers and filenames are used only after inference for reporting; no model proposal is supplied to inference. Exact duplicate dialogue with the same catalog is evaluated once and reused automatically. There are no prediction corrections or inference replays.

CPU-only and explicit thinking-off settings are required. The runner verifies the installed model checksum, waits for Scout to be idle, uses a separate loopback port, and stops its own runtime if user processing resumes. Only workspace status is read from the application database. It never writes application data or reads video files. Original cues and provider guide descriptions come from the fixture. Requests, raw responses, stages, token counts, cache reuse, timings, decisions and reference comparisons are checkpointed in the isolated `benchmark.db`. `results.html` contains the new outcomes only.

References are inherited model/evaluation labels, not independent ground truth. Report accepted disagreements separately from held suggestions and held non-episode references. Older timings used different prompt revisions and validation rules; compare like stages and acceptance changes rather than claiming a controlled speedup. The Farpoint case now includes candidate discovery; earlier runs measured coverage audits of a saved proposal only.

Finalization applies the production collection resolver to the fresh results and saved recording identity proofs. It groups proven duplicates and holds competing assignments, without changing any model output or invoking inference. The empty-output requirement prevents accidentally overwriting or rerunning completed inference. A failed benchmark retains all completed requests and case checkpoints for diagnosis.

Prompt-processing threads are configured separately with `settings.prompt_threads`; zero uses `settings.cpu_threads`, which controls generation. `--tune-threads fixture.json empty-output-directory` screens 8, 12, 16 and 24 threads using the installed CPU `llama-bench` and model, two 1,024-token prompt repetitions each. This screening does not substitute for measured episode runs and never changes application settings.

An explicit top-level fixture flag `try_excerpt_verification: true` enables the experimental excerpt audit. The default proceeds directly to complete-dialogue verification. If a completed experiment accepted no matches from excerpts alone, `--validate-default results-directory` verifies the simpler default using the exact remaining requests and untouched saved responses. It fails on any changed prompt, schema, request parameter, assignment, coverage, evidence, or validation outcome. Only revision and redundant routing diagnostics may differ. It adds a validation document to the isolated benchmark and does not rerun inference or replace the measured outcomes. Omitted-call durations are recorded measurements of those calls, not a new measurement of the default workflow. Run `--finalize` afterward to include this qualification in the report.

For a post-processing change that does not alter prompts or model inputs, `--revalidate completed-results-directory empty-output-directory` replays every recorded request through the production matcher. It rejects any new or different request, preserves raw model responses, and checks that previously accepted outputs remain identical apart from revision metadata. Timings remain those of the original measured inference, with offline revalidation time reported separately. Source IDs identify exact original locations, rather than free-form quotations. If a split leaves a short tail, lookup retrieves adjacent original words from the same block and records the selected fragment, context span and whether that context was separately cited. It cannot cross a block, fabricate text, change a verdict, or relax the free-form quote validator. Intrinsically too-short blocks remain invalid. The source benchmark is read-only.
