# Code signing policy

**Status:** Dropstone is preparing an application for the SignPath Foundation free code-signing program. Release binaries remain unsigned until the application is approved and the signed release workflow is enabled.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## Scope

The signed artifact is `Dropstone.exe` inside `Dropstone-<version>-win-x64.zip`. ZIP archives do not carry Authenticode signatures; verify the extracted EXE.

## Team roles

| Role | Members |
| --- | --- |
| Authors | [@TheNevinPie](https://github.com/TheNevinPie) |
| Reviewers | [@TheNevinPie](https://github.com/TheNevinPie) |
| Approvers | [@TheNevinPie](https://github.com/TheNevinPie) |

All team members use multi-factor authentication for GitHub and SignPath. Changes from non-committers require review by a team member before merge. Every signing request is approved manually by the approver.

## Release and signing process

1. The version in `src/Dropstone/Dropstone.csproj` is updated and a `v<version>` tag is pushed.
2. The GitHub Actions workflow builds the EXE from the tagged source and runs the worker tests.
3. The build is submitted to SignPath for signing.
4. The approver reviews the source commit, workflow run, and artifact, then approves the signing request in SignPath.
5. The signed EXE is packaged into the release ZIP.
6. The release notes include the SHA256 of the ZIP and EXE and link to this policy.

## Verification

```powershell
Get-AuthenticodeSignature .\Dropstone.exe | Format-List
Get-FileHash .\Dropstone.exe -Algorithm SHA256
Get-FileHash .\Dropstone-1.0.0-win-x64.zip -Algorithm SHA256
```

A signed release shows a valid Authenticode signature from SignPath Foundation. Compare the SHA256 values with the release notes.

## Privacy

See [PRIVACY.md](PRIVACY.md). Dropstone does not collect telemetry or analytics. It only accesses the network when the user configures and starts a payload URL download.

## Reporting

Report a signing or security concern through [GitHub Issues](https://github.com/TheNevinPie/Dropstone/issues).
