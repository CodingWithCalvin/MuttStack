# Tucker to Scout handoffs

This workflow uses the shared Doggo database. Its tests run against isolated databases; building and packaging do not change a live deployment.

## User flow

Select a saved Tucker and choose **Send to Scout**. Only completed, published compressed outputs are included. Files still waiting or encoding can be sent later as another batch. Raw inputs, encoding settings and media files are unchanged.

Tucker displays **awaiting Scout**, **accepted by Scout**, or **declined by Scout**, with file counts. It checks on startup and every five seconds. Scout does not need to be running when the batch is sent, and Tucker does not need to be running when Scout responds.

Scout's **Inbox** shows a waiting badge and the exact output paths before acceptance. **Accept into Scout** saves draft Scouts grouped by series provider/ID and filesystem root, with an explicit file selection. Unknown series are left for discovery. Discovery for these drafts uses the handed-off selection, not unrelated files beside it. Acceptance does not launch identification, download guides, load models, or move files. The user opens a saved Scout and starts it explicitly.

Decline creates a durable response without creating work. Declined files are not automatically resent. There is no reset/resend-declined command in this initial workflow.

## Ownership and compatibility

SQLite uses table prefixes as logical schemas. Existing components stay at `doggo=1`, `handoff=1`, `tucker=2`, and `scout=1`. This adds three version-1 components:

| Component | Owned tables | Purpose |
| --- | --- | --- |
| `handoff_routes` | `handoff_routed_batches`, `handoff_routed_events` | Explicit recipient, contract, frozen payload, response and events |
| `tucker_handoffs` | `tucker_scout_outbox`, `tucker_scout_sent_files` | Durable sending intent and duplicate prevention |
| `scout_handoffs` | `scout_handoff_receipts`, `scout_handoff_files` | Durable acceptance and output-to-Scout mapping |

The legacy `handoff_batches` and `handoff_events` remain exclusively Ripley â†’ Tucker. Older recipient-unaware apps never see Scout offers in those tables. No peer app's private schema is migrated by the transport.

Tucker owns `src/Doggo.Handoffs/ScoutHandoff.cs`; Scout owns its independently maintained receiver adapter. Neither repository references, source-links or loads code from the other. Their compatibility boundary is the [database contract](doggo-contract.md).

## Contract and transactions

`tucker.scout.outputs` version 1 has `SourceApp=tucker`, `TargetApp=scout`, a stable GUID batch ID, source run ID/name, and one entry per completed output. Each entry retains job ID, absolute compressed path, size, UTC modification time, optional catalog series identity, season, volume, disc, label, rip date, and original filename. A SHA-256 digest covers the exact UTF-8 JSON payload. Size and modification time detect missing/changed media; they are not a media-content checksum.

Tucker commits its outbox payload and file claims first. Delivery then inserts the shared offer. If delivery fails, startup/polling retries the exact saved ID and payload. Reusing an ID with different content is rejected. Archive or deletion of the source run does not remove sending intent or claims.

Scout accepts under one immediate SQLite transaction: validate route/version/digest and new files; create draft workspace documents; save the receipt and file ledger; mark the offer accepted; append an acceptance event. Any error rolls back all of it. Separate or concurrent accepts return the original receipt. The receipt remains valid after Scout moves media or the user deletes its saved run. Overlapping batches reuse existing file mappings; conflicting metadata is refused. Decline cannot overwrite acceptance.

## Validation and deployment boundary

Tucker's core and WPF suites cover sending, frozen retry, concurrent sends, legacy inbox isolation, restart status, metadata, and later additions. Scout's suites cover recipient filtering, exact selection, UI acceptance/decline, concurrent acceptance, missing/changed media, overlap, and forced transaction rollback.

Scout's own `tools/Scout.HandoffRehearsal` tests frozen synthetic producer messages and its real receiver in separate processes. It can use a fresh isolated database or copy an existing offline migration snapshot and verify pre-existing rows and the source snapshot hash. It requires only Scout's checkout.

Deployment requires idle-app backups and compatible database contracts. Ripley and Tucker read protected credentials directly from Doggo; Scout alone owns its persistent storage selection and cutover tool. Packaging and tests do not perform a live migration, install an app or modify existing media.