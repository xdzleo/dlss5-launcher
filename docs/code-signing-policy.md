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
   `dotnet publish -c Release -r win-x64 --self-contained` with no post-processing.
3. The resulting artifact is submitted to SignPath.io via the official
   `signpath/github-action-submit-signing-request` action.
4. Each signing request is **manually approved** by the project owner in the SignPath dashboard
   before the certificate is applied.
5. The signed binary is attached to the GitHub Release. Only signed artifacts are distributed.

## Account security

- The project owner's GitHub and SignPath accounts have **multi-factor authentication** enabled.
- The SignPath API token is stored only as an encrypted GitHub Actions secret
  (`SIGNPATH_API_TOKEN`) and is never exposed in logs.

## Privacy

The application does not collect or transmit personal data. It reads local game-library metadata,
downloads ReShade and RenoDX mod files from their official sources, and edits configuration files
in the user's game folders. See the [README](../README.md) for details.
