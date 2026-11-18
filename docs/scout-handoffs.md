# Tucker to Scout handoffs

This workflow is implemented and tested on isolated databases. It has not been installed over the live apps or enabled against live Scout storage.

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

The legacy `handoff_batches` and `handoff_events` remain exclusively Ripley → Tucker. Older recipient-unaware apps never see Scout offers in those tables. No peer app's private schema is migrated by the transport.

The shared source lives in `src/Doggo.Handoffs/ScoutHandoff.cs`. Tucker uses the library; Scout source-links the same protocol and native SQLite wrapper from a sibling Tucker checkout. This lets .NET 9 Tucker and .NET 11 Scout compile the same implementation without a runtime dependency on the other app. Published apps remain independent. Keep the two source revisions coordinated until the protocol is packaged separately.

## Contract and transactions

`tucker.scout.outputs` version 1 has `SourceApp=tucker`, `TargetApp=scout`, a stable GUID batch ID, source run ID/name, and one entry per completed output. Each entry retains job ID, absolute compressed path, size, UTC modification time, optional catalog series identity, season, volume, disc, label, rip date, and original filename. A SHA-256 digest covers the exact UTF-8 JSON payload. Size and modification time detect missing/changed media; they are not a media-content checksum.

Tucker commits its outbox payload and file claims first. Delivery then inserts the shared offer. If delivery fails, startup/polling retries the exact saved ID and payload. Reusing an ID with different content is rejected. Archive or deletion of the source run does not remove sending intent or claims.

Scout accepts under one immediate SQLite transaction: validate route/version/digest and new files; create draft workspace documents; save the receipt and file ledger; mark the offer accepted; append an acceptance event. Any error rolls back all of it. Separate or concurrent accepts return the original receipt. The receipt remains valid after Scout moves media or the user deletes its saved run. Overlapping batches reuse existing file mappings; conflicting metadata is refused. Decline cannot overwrite acceptance.

## Validation and deployment boundary

Tucker's core and WPF suites cover sending, frozen retry, concurrent sends, legacy inbox isolation, restart status, metadata, and later additions. Scout's suites cover recipient filtering, exact selection, UI acceptance/decline, concurrent acceptance, missing/changed media, overlap, and forced transaction rollback.

The sibling Scout tool `tools/Scout.HandoffRehearsal` exercises the actual sender and receiver in separate processes against a copy of an **existing offline migration snapshot**, using only synthetic media. It verifies every pre-existing database row, the source snapshot hash, integrity and foreign keys. See its README for commands.

Live rollout still requires fresh idle-app backups, a fresh Scout migration rehearsal, coordinated installation, and an explicit Scout storage switch. Ripley and Tucker now share a credential reader that follows Scout's persisted `storage.json` selection. They must be installed with the matching Scout build before switching. Scout's separate cutover tool prepares and verifies backups, imports only Scout's tables, saves the selection, retires the old standalone writer, and supports guarded resume/rollback. These changes have been tested only on isolated copies. Normal Scout startup uses its standalone database until the cutover tool saves a shared selection. Neither live migration nor installation is performed by these tests.
