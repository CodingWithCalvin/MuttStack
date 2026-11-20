# Tucker Windows installers

This repository owns its installer script, build scripts and workflow. No companion checkout or shared private code is required. Apps integrate through the [Doggo database contract](doggo-contract.md).

## Build and test

Use Windows, PowerShell 7, and the .NET SDK pinned in `global.json`. Run:

```powershell
./eng/test.ps1
./eng/package.ps1 -Version 0.1.0.0 -OutputDirectory "$PWD/artifacts/distribution"
./eng/test-installer.ps1
```

`-DotnetPath` accepts an explicit dotnet host. Scout currently uses its pinned .NET 11 preview; Ripley and Tucker use .NET 9. Packaging provisions the official Inno Setup 7.1.1 x64 compiler under ignored `artifacts/tools`, verifies the download against a pinned SHA256, and uses upstream portable mode without registering the compiler or creating shortcuts. It does not require Visual Studio, Windows SDK packaging tools, a signing certificate, or a paid signing service.

Choose a fresh output directory on each build. Outputs are:

- `Tucker-VERSION-win-x64-setup.exe`: unsigned per-user installer.
- `Tucker-VERSION-win-x64.zip`: portable application folder.
- `SHA256SUMS.txt`: checksums for both downloads.

Both packages include their own .NET runtime; users do not need to install .NET separately. Windows 10 version 2004 or newer on an x64-compatible system is required. Tucker also includes the pinned HandBrake CLI, upstream notices and corresponding source archive. MakeMKV and FFmpeg/FFprobe remain separately configured tools; models, credentials and media are never packaged.

## Install, upgrade and uninstall

The installer defaults to `%LOCALAPPDATA%/Programs/Coding With Calvin/Tucker`, requests no administrator elevation, and creates a Start menu shortcut plus a per-user uninstall entry. It never launches the app, starts processing, or installs a background updater. Open the app from the Start menu when ready.

The stable app identity is `CodingWithCalvin.Tucker`. Keep it unchanged across releases. A newer installer updates the same directory and uninstall entry; the same version can be reinstalled. Older versions are refused. Install and uninstall check for files in use and refuse to continue until the user closes the app and its tools. They never force-close processing, schedule a reboot replacement, or restart an app.

Settings and model caches remain in their existing per-user locations. Doggo remains `%LOCALAPPDATA%/Doggo/doggo.db`; Scout retains its own persistent database selection. Installers do not migrate, copy, merge or delete app databases. Uninstall removes installer-owned files, its shortcut and its registration, preserving saved data, media and any unknown files added to the application directory. Existing external tool paths are preserved; when moving from a portable folder, review those paths before deleting the old folder.

Local installer tests compile a validation variant with a unique identity, install directory and shortcut location under `artifacts/validation`. Only those locations differ from the production installer. The tests cover exact payload hashes, per-user registration, shortcuts, the installed app's isolated UI suite, file-lock refusal, upgrades, downgrade refusal, reinstall, uninstall and preservation of synthetic data. They never launch the real app entry point or open live state. On a clean GitHub-hosted runner, `eng/test-installer.ps1 -Production` additionally installs the actual release installer and exercises its real per-user registration and Start menu paths. That switch is refused outside a GitHub-hosted runner.

## GitHub releases and signing

Pull requests, main pushes and manual workflows build and test this repository, then upload the installer, ZIP and checksums. A `vMAJOR.MINOR.PATCH` or four-component version tag additionally creates a **draft** GitHub release after all checks pass. No signing secrets, Azure account or certificate configuration are needed. Public publishing remains a separate review step.

These installers and app executables are unsigned. Windows may show an unknown-publisher or SmartScreen warning, and some security policies can block unsigned applications. Changing installer format does not bypass Windows security. Users should verify the GitHub source and download before deciding whether to run it. Signing can be added later without changing installer technology or application identity.

The previous MSIX packaging/signing scripts have been replaced. Existing MSIX test packages are separate historical artifacts; they are not upgraded or uninstalled by this installer. Inno Setup's authors request commercial license purchases for qualifying commercial use, but their current terms do not make payment mandatory.

References: [Inno Setup](https://jrsoftware.org/inno-setup), [license terms](https://jrsoftware.org/inno-setup/purchase), [Windows download reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
