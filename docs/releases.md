# Scout Windows packages

This repository builds independently. No companion checkout, shared source tree, or shared private library is required. App integration is through the versioned Doggo SQLite contract documented in [doggo-contract.md](doggo-contract.md).

## Local build

Use Windows, PowerShell 7, the SDK in `global.json` and Windows SDK tools (`makeappx.exe`, `signtool.exe`). Scout currently uses its pinned .NET 11 preview SDK; the other apps use .NET 9. `-DotnetPath` accepts an explicit SDK host when needed.

```powershell
./eng/test.ps1
./eng/package.ps1 -Version 0.9.12.0 -OutputDirectory "$PWD/artifacts/distribution"
./eng/test-published.ps1
```

Choose a fresh output directory on each run. Packaging never writes the existing portable app folder, installs an app, changes a database, or starts an encode/model. Outputs are a self-contained Windows x64 portable ZIP, an **unsigned** MSIX, and SHA256 checksums. Both carry the app's own runtime, so a separately installed .NET runtime is unnecessary. MSIX requires Windows 10 version 2004 or newer. It uses full-trust desktop activation and disables AppData file virtualization so the three apps keep the same `%LOCALAPPDATA%/Doggo/doggo.db` and data survives package uninstall. Files in the installation directory remain read-only; all settings, caches and models stay outside it.

The app's database schema and settings are not imported by the installer. Existing Scout storage selection is respected. MakeMKV and FFmpeg/FFprobe remain separately configured tools. Tucker packages include the pinned HandBrake CLI, its notices, and the corresponding source archive. Model downloads and API credentials are never included.

## Signing and releases

The package publisher defaults to `CN=Coding With Calvin`. Keep the identity and publisher stable for upgrades. Set repository variable `MSIX_PUBLISHER` to the exact subject of the production signing certificate **before** the first public release.

In each repository's GitHub `release` environment, configure `MSIX_CERTIFICATE_BASE64` (base64-encoded code-signing PFX) and `MSIX_CERTIFICATE_PASSWORD`. Do not commit a certificate or paste private keys into an issue. The signing script imports it only for the signing operation, removes its temporary file/key afterward, uses a SHA256 timestamp, and verifies the production trust chain. A hardware-backed signing service can replace this step later; none is assumed configured.

Pull requests, main-branch pushes and manual runs build/test this repository and upload unsigned MSIX plus portable ZIP artifacts. Only a `vMAJOR.MINOR.PATCH` (or four-component) tag starts the signing job. All tests and packaging must pass first. Missing signing secrets fail the release job; it never silently publishes an unsigned installer. Successful signing creates a **draft** GitHub release for review, not an automatic public release.

For local signature validation only:

```powershell
./eng/sign-package.ps1 -Package ./artifacts/distribution/Scout-0.9.12.0-win-x64-unsigned.msix -Development
```

This makes a short-lived test certificate, signs the package, exports only its public `.cer`, then removes the private key. It does **not** trust the certificate, register/install the package, or change an existing app. A test-signed MSIX requires explicit certificate trust on a test machine; use a production-trusted certificate for public distribution. Never distribute a new test signing identity as a production upgrade.

References: [MSIX shared AppData behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization), [MSIX signing](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).
