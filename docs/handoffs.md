# Doggo handoff contract v1

Ripley now writes the shared inbox directly through Doggo.Handoffs. It first saves an immutable UTF-8 outbox manifest under its state folder and freezes the history entry. Failed delivery retries that same manifest/ID. Explicit JSON import remains available. Apps do not need to run simultaneously.

```json
{
  "Version": 1,
  "Id": "205cb3ca-6786-4b48-90bd-36ed885bdf52",
  "SourceApp": "ripley",
  "TargetApp": "tucker",
  "Files": [
    {
      "Source": "R:\\Raw\\Example\\1_2_title_t00.mkv",
      "SourceSize": 123456789,
      "SourceModified": "2026-11-07T18:00:00Z",
      "Series": {"Provider":"tvdb","Id":"123","Name":"Example","Year":"2026","Overview":""},
      "Season": "1",
      "VolumeNumber": "1",
      "DiscNumber": "2",
      "DiscLabel": "EXAMPLE_S1_V1_D2",
      "DiscId": "stable-disc-run-id",
      "RipDate": "2026-11-07T18:00:00Z"
    }
  ]
}
```

The sample is illustrative, not importable test media. Producers supply actual file sizes and exact UTC modification timestamps after raw files and metadata are safely published. Files must be completed MKVs, outside rip staging and outside the selected output tree. Metadata is per file; multiple disc IDs create separate queues. Ripley handoffs retain their published filenames; Tucker does not prepend its external-import prefix again.

Use a stable GUID per offered batch. Retries preserve the same bytes: v1 hashes the input bytes with SHA-256, so reformatting the JSON under the same ID is a content conflict. Accepted imports are idempotent and an ID cannot be repurposed. Files are additionally deduplicated by canonical path, size and UTC modification timestamp; this is a change detector, not a cryptographic media-content hash.

Offered is durable inbox delivery, not queue acceptance. Review uses Tucker's selected preset/output settings. Adding all files commits queue records and the Accepted receipt together; jobs remain waiting unless the user explicitly chooses Add to Running Queue. Declining leaves files untouched. An exported receipt has Id, Digest, SavedUtc and Files (new jobs accepted; zero is possible when sources were already imported).

Ripley shows Awaiting Tucker after offering and Accepted by Tucker only after reading the committed receipt. It prevents local compression and repeat offers while awaiting acceptance. It checks on startup and every five seconds while idle; database availability failures leave the lock in place. Declined runs remain frozen for explicit follow-up. Process notification is optional; the database is authoritative on startup and refresh. Existing Ripley compression jobs must not also be sent to Tucker without an explicit duplicate-work review.

Future Tucker-to-Scout offers should contain only published outputs and use their own contract/version. Scout's receiving UI, producer/consumer protocol extraction, and migration of either existing app remain separate work.
