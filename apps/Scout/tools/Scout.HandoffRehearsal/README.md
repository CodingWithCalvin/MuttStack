# Isolated Mutt Stack contract rehearsal

This tool uses frozen synthetic Tucker-format contract messages and Scout's real receiver. It compiles only Scout-owned code. No companion checkout, encoder, model, live executable or live database is opened.

Build with Scout's pinned SDK, then use a new guarded `.build/handoff-rehearsal-*` directory:

```powershell
dotnet build tools/Scout.HandoffRehearsal -c Release
$handoffRoot = Join-Path $PWD '.build/handoff-rehearsal-new'
foreach ($phase in @('prepare','send','accept','append','accept','verify')) {
    dotnet run --project tools/Scout.HandoffRehearsal -c Release --no-build -- $phase $handoffRoot
}
```

Each invocation is a separate process. The fixture offers two files, Scout accepts them into waiting drafts, another client observes the database acknowledgement after Scout closes, and a later file is offered and accepted. Retries retain the same ID and payload. Verification checks receipts, pre-existing rows, SQLite integrity and foreign keys. No processing starts.

Optionally pass an existing `.build/doggo-rehearsal-*/doggo.migrated.db` offline snapshot as the third argument to `prepare`. The tool copies it and verifies its source hash afterward; it refuses existing output directories and junction ancestors. Such copies can contain private state and remain under ignored `.build`. `baseline.json` stores row hashes; `handoff-report.json` stores the result.

The `selection-handoff` and `selection-status` modes verify an isolated `.build/cutover-rehearsal-*` switch through Scout's own persistent selector. It must point to that directory's `Doggo/doggo.db`. See the [cutover tool](../Scout.DatabaseCutover/README.md) and [database contract](../../docs/doggo-contract.md).
