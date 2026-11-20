# Tucker-owned Doggo database adapter

This implementation belongs to Tucker. It is compiled only from this repository and is not synchronized, source-linked, or packaged for use by another app. Its initial implementation was adapted from the MIT-licensed Doggo adapter in Tucker at commit 362596e3a6f38695458c3844b4a19acc70460204; subsequent changes are owned here.

The compatibility boundary is the versioned SQLite schema and serialized handoff contract, never a .NET type or a sibling checkout. See this repository's `docs/doggo-contract.md`. Database `.access` files are storage-maintenance locks, not messages or another app's settings. Credential lookup opens the configured shared database directly; it does not consult Scout's private configuration or legacy credential files.
