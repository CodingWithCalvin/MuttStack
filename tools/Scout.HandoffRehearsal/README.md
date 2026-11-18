# Isolated Tucker → Scout rehearsal

Uses the real Tucker sender and Scout receiver with synthetic media. No encoders, model servers, installed app executables, or live databases are opened. The tool deliberately accepts only an existing `.build/doggo-rehearsal-*/doggo.migrated.db` snapshot as its source. Preparation creates a new copy and refuses existing output directories and junctions.

Build with Scout's pinned .NET SDK and a sibling `Tucker` checkout containing the matching handoff implementation. `Scout.Core` source-links the shared protocol. This tool additionally source-links the Tucker core files used by its sender tests.

From the Scout repository:

```powershell
dotnet build tools/Scout.HandoffRehearsal -c Release -m:1
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$handoffRoot = Join-Path $PWD '.build/handoff-rehearsal-new'
$offlineSnapshot = Join-Path $PWD '.build/doggo-rehearsal-EXISTING/doggo.migrated.db'
$project = 'tools/Scout.HandoffRehearsal'

dotnet run --project $project -c Release --no-build -- prepare $handoffRoot $offlineSnapshot
if ($LASTEXITCODE -ne 0) { throw 'Preparation failed' }
foreach ($phase in @('send', 'accept', 'append', 'accept', 'verify')) {
    dotnet run --project $project -c Release --no-build -- $phase $handoffRoot
    if ($LASTEXITCODE -ne 0) { throw "Rehearsal failed: $phase" }
}
```

Each invocation is a separate process. First Tucker sends two completed outputs from different series, while a third remains waiting. Scout accepts while the sender process is closed. Reopened Tucker sees the response and offers the third file after its simulated completion. Scout accepts it, and a final sender restart verifies both responses and refuses duplicate delivery.

`handoff-report.json` records the result. `baseline.json` contains row hashes, not plaintext document contents. The copied database retains private records from the snapshot, so the entire rehearsal directory stays under ignored `.build`. Verification requires all existing rows to retain their original values, including encrypted credential bytes, legacy handoffs, and Ripley/Tucker/Scout records. It also checks the original snapshot hash, SQLite integrity and foreign keys.

This tool does not switch Scout storage or publish either application. To test UI controls without launching an installed app, run `Scout.Desktop.Tests`; it uses temporary settings, workspaces and synthetic data. Set the process-local `SCOUT_TEST_RENDERS` variable to an isolated directory to save the inbox render.

The protocol and live-rollout boundary are documented in the sibling Tucker repository's `docs/scout-handoffs.md`.

The `selection-handoff` and `selection-status` modes also verify a completed `.build/cutover-rehearsal-*` switch through Scout's persistent selector. They require that selector to point to the isolated directory's `Doggo/doggo.db`. See the [cutover tool](../Scout.DatabaseCutover/README.md) for the sequence.
