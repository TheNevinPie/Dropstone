# SignPath Foundation application preparation

Status: preparing now, applying after the project has enough verifiable reputation.

## Eligibility checklist

- [x] Public GitHub repository
- [x] Released software
- [x] Documented functionality
- [x] MIT license
- [x] Privacy policy
- [x] Code signing policy
- [ ] Release/download page mentions the code signing policy
- [x] GitHub Actions build workflow
- [ ] Verifiable reputation
- [ ] SignPath account and application
- [ ] MFA confirmed on GitHub and SignPath
- [ ] SignPath project and artifact configuration
- [ ] Signed release workflow and test release

## Current reputation

As of 2026-10-21:

- 10 GitHub releases since 2026-10-19
- 1 star and 0 watchers
- 2 downloads of the v1.0.0 asset
- No media coverage or independent reviews yet

This is below SignPath's stated requirement for a verifiable reputation for executable programs. Grow real usage and references before submitting.

## Draft application answers

- Project Name: Dropstone
- Repository URL: https://github.com/TheNevinPie/Dropstone
- Homepage URL: https://github.com/TheNevinPie/Dropstone
- Download URL: https://github.com/TheNevinPie/Dropstone/releases/latest
- Privacy Policy URL: https://github.com/TheNevinPie/Dropstone/blob/main/PRIVACY.md
- Wikipedia URL: leave blank
- Tagline: Keeps user-provided mod payloads in place across LeviLauncher Minecraft Bedrock updates.
- Category: Developer Tools, or the closest available category
- Description: Dropstone is a portable WinUI 3 installer and per-user sync worker for LeviLauncher. It deploys a user-supplied ZIP, RAR, or DLL payload into Minecraft Bedrock version folders and restores the payload when the launcher creates a new version. It does not bundle, download, or promote game files or third-party payloads; the user supplies the payload. It includes uninstall support and a documented privacy policy.
- Reputation: fill with real GitHub insights, download counts, community posts, and independent coverage after they exist
- Maintainer Type: Individual
- Build System: GitHub Actions
- First Name, Last Name, Email: the applicant fills these
- Company Name: leave blank unless applicable
- Primary Discovery Channel: Web search
- Exact source: search for free open-source code signing

## Policy risks

SignPath prohibits circumvention tools. The public repository ships no payload and its README disclaims license-bypass use, but the same installer can deploy a user-supplied unlocker. Describe the software accurately as a payload deployment utility, and be prepared to explain that policy note and the no-payload design if SignPath asks. If the project is primarily promoted as an unlocker, it will likely be rejected.

SignPath also requires a verifiable reputation. Do not submit until there are real users, download numbers, community discussions, or coverage to cite.

## After approval

1. Install the SignPath GitHub integration and store the API token in GitHub Actions secrets.
2. Create a SignPath project, signing policy, and artifact configuration for `Dropstone.exe`.
3. Enforce the product name `Dropstone` and a single product version in the artifact configuration.
4. Add the SignPath signing step to `.github/workflows/build.yml` after the build and before zipping.
5. Require manual approval for every signing request.
6. Test the signature with `Get-AuthenticodeSignature` and publish the signed EXE's SHA256 in the release notes.
7. Update `CODE_SIGNING.md` to remove the unsigned status once the first signed release is published.

The SignPath Foundation certificate is issued to SignPath Foundation, so the publisher shown in the signature will be SignPath Foundation rather than TheNevinPie.
