# Code Signing Policy

RenoDX Launcher's Windows releases are code-signed through the
[SignPath Foundation](https://signpath.org/) free code-signing program for open-source projects.

## Committers and reviewers

- **Author / Reviewer / Approver:** [@xdzleo](https://github.com/xdzleo) (project owner)

All commits are made by, or reviewed by, the project owner before a release tag is created.

## Build & signing process

1. Source is public at <https://github.com/xdzleo/renodx-launcher> under the MIT license.
2. Releases are built **exclusively by GitHub Actions** (`.github/workflows/release.yml`) on a
   clean hosted runner — never on a local machine. The build is a standard
   `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=false`.
   The only post-processing is the removal of `createdump.exe` (an unused .NET diagnostic
   tool) from the publish output, done by an MSBuild target in the project file.
3. The application binary is submitted to SignPath.io via the official
   `signpath/github-action-submit-signing-request` action.
4. **Only after `RenoDXLauncher.exe` comes back signed** is the Inno Setup installer built
   around it (`installer/RenoDXLauncher.iss`, Inno Setup pinned by version and SHA-256).
   The installer is then submitted as a second signing request. This ordering means the
   binary that ends up on the user's disk is signed, not just the installer that ran once.
5. Each signing request is **manually approved** by the project owner in the SignPath dashboard
   before the certificate is applied.
6. Both signed artifacts are attached to the GitHub Release, together with a `SHA256SUMS.txt`.
   The workflow verifies each artifact with `Get-AuthenticodeSignature` and fails the release
   if anything comes back unsigned or tampered with. Only signed artifacts are distributed.
7. Every build — release or not — runs `tools/av-selfcheck.ps1`, which asserts that no
   code-injection or persistence API (`WriteProcessMemory`, `CreateRemoteThread`,
   `SetWindowsHookEx`, scheduled tasks, services, …) has entered the source tree.

## Account security

- The project owner's GitHub and SignPath accounts have **multi-factor authentication** enabled.
- The SignPath API token is stored only as an encrypted GitHub Actions secret
  (`SIGNPATH_API_TOKEN`) and is never exposed in logs.

## Privacy

The application does not collect or transmit personal data. It reads local game-library metadata,
downloads ReShade and RenoDX mod files from their official sources, and edits configuration files
in the user's game folders. See the [README](../README.md) for details.
