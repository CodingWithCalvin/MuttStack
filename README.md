# Mutt Stack

Scout, Ripley, and Tucker: independently owned apps, released together.

| App | Purpose | Code |
| --- | --- | --- |
| Ripley | Rip discs with MakeMKV and hand completed batches to Tucker | [apps/Ripley](apps/Ripley) |
| Tucker | Compress recordings with HandBrake and hand completed output to Scout | [apps/Tucker](apps/Tucker) |
| Scout | Identify episodes from subtitles and dialogue, then preview and apply library naming | [apps/Scout](apps/Scout) |

Each app owns its source, dependencies, database adapters and tests. Applications do not reference or call one another's code. Their only runtime interaction is the shared database contract, including durable inbox handoffs. Shared repository files are build, installer and release tooling only.

All three apps use one version and one GitHub Actions build/release pipeline. A version tag builds all apps and produces one draft release containing all three installers. See [releases and local builds](docs/releases.md).

Full histories from the original repositories are preserved as merge ancestry; see [import provenance](docs/imports.json). The original repositories remain unchanged. Historical `Doggo` names in storage and protocol identifiers are retained for compatibility; the product collection is now **Mutt Stack**.
