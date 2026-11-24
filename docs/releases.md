# Mutt Stack releases

Scout, Ripley and Tucker are released together from this repository. The single version lives in `build/MuttStack.props`; apps display its three-part value and Windows files/installers use the same value with a `.0` revision. CI build numbers never change an individual app's version. Build provenance records the exact monorepo commit.

Every pull request, push to `main`, manual workflow run and version tag builds and tests **all three apps** with the pinned root SDK and the same scripts. Ripley and Tucker retain their existing .NET 9 targets; Scout retains .NET 11. CI installs the .NET 9 runtime alongside the root SDK for tests. Self-contained installers include their required runtime.

## Preparing a release

1. Update `MuttStackVersion` in `build/MuttStack.props`, then merge after all three build/installer jobs pass.
2. Tag that commit `vMAJOR.MINOR.PATCH` and push the tag. The workflow rejects tags that differ from the root version.
3. The release job waits for every app's tests and installation/upgrade checks, validates all three installers and checksums, then creates **one draft Mutt Stack release**. Review and publish that draft to make all three installers available together. No individual app release job exists.

The first coordinated version is 0.10.0, above all imported app base versions. Re-running an existing release tag does not silently replace published files. Fix the problem, increment the shared version, and release all apps again.

## Downloads and installation

Releases contain three unsigned `*-win-x64-setup.exe` installers and one `SHA256SUMS.txt`. There are no portable ZIP distributions. GitHub's workflow artifact download envelope is separate from the installer distribution format.

Installers require Windows 10 build 19041 or later, run per user, and do not require administrator access. Windows may display a reputation warning for an unsigned download. They refuse to replace locked app files and never start the applications, migrate a database, or access media during installation.

Existing app IDs, install directories, Start menu shortcut names and storage paths remain compatible. Installed Apps identifies each as **Mutt Stack - Scout**, **Mutt Stack - Ripley** or **Mutt Stack - Tucker**. The `Doggo` data directory, database filename, schemas, credential scopes and protocol identifiers retain their legacy names deliberately; rebranding does not create or migrate a new database.

## Local build and validation

Install the SDK pinned in `global.json` plus .NET 9 for Ripley/Tucker tests. From the repository root, using PowerShell 7:

```powershell
./eng/check-independence.ps1
./eng/test.ps1 -Application Scout
./eng/package.ps1 -Application Scout
./eng/test-installer.ps1 -Application Scout
```

Repeat with `Ripley` and `Tucker`. `-DotnetPath` selects a private SDK. Local installer tests use a separate registration ID and private install/state/media directories; `-Production` is restricted to a fresh GitHub-hosted runner. Test harnesses run against isolated state and synthetic media. Packaging itself never installs an app. App-local `eng/*.ps1` files are compatibility entry points to these scripts.
