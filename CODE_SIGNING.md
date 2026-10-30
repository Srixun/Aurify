# Code signing policy

## Status and scope

SignPath/SignPath Foundation onboarding is pending. Nothing in this repository claims provider acceptance, certificate issuance, or that a current binary has a valid signature.

The only artifact submitted to SignPath is `Aurify.exe`. The model-runtime descriptor is embedded in that executable and is therefore covered as part of its Authenticode-signed bytes. A standalone `Aurify-models.json`, `Aurify-models.zip`, source archives, installers from other projects, and the separately downloaded wow.export-derived runtime must not be submitted, independently signed, or represented as Aurify-signed artifacts.

## Current roles

Aurify currently has one maintainer:

- author: [@Srixun](https://github.com/Srixun);
- reviewer: [@Srixun](https://github.com/Srixun); and
- signing approver: [@Srixun](https://github.com/Srixun).

These roles describe the current small-project reality rather than claiming separation of duties. Any future role change must be reflected here and in repository/provider access controls before it is used.

## Required controls

1. The maintainer must enable multi-factor authentication for both GitHub and SignPath accounts. Shared accounts and shared API tokens are prohibited.
2. Release builds must run only on the GitHub-hosted `windows-2022` runner from the checked-in `.github/workflows/release-signing.yml`. Self-hosted runners are prohibited for signing candidates. The workflow installs the exact .NET SDK selected by `global.json`; Roslyn deterministic compilation uses fixed payload timestamps, stable path mapping, explicit framework references, and records SHA-256/length fingerprints for the .NET host, compiler, and references in `Aurify-release.json`. The workflow is limited to `refs/heads/main`, and `main` must be protected against unreviewed direct or force pushes.
3. Create a GitHub Environment named `code-signing`. Restrict deployment to the protected `main` branch and require maintainer review. Store `SIGNPATH_API_TOKEN` only as a secret in that Environment—never as a repository or organization secret. A workflow dispatched from another ref must not be able to receive it.
4. The signing request must use the GitHub artifact ID produced by the same workflow run. The SignPath project must verify the trusted origin: this repository, the release-signing workflow, `refs/heads/main`, the exact commit shown in the run, a GitHub-hosted runner, and the expected unsigned artifact. A request with an unverified or mismatched origin must be rejected.
5. The fixed provider identifiers are project `aurify`, signing policy `release-signing`, and artifact configuration `aurify-exe`. The artifact configuration must accept exactly one PE file named `Aurify.exe` and enforce `ProductName` `Aurify`, `ProductVersion` `2.0.6.0`, and `FileVersion` `2.0.6.0`. The workflow may read only the Environment secret `SIGNPATH_API_TOKEN` and the `SIGNPATH_ORGANIZATION_ID` GitHub variable for submission. Values must never be committed or printed.
6. A signing approver must manually review the source commit, GitHub-hosted workflow run, unsigned `Aurify.exe`, separately uploaded `Aurify-release.json`/`Aurify-models.json` provenance, requested fixed slugs, artifact digest, and artifact contents before approving the request in SignPath. Automatic approval is prohibited.
7. The workflow must wait for SignPath completion, download exactly one signed `Aurify.exe`, and require PowerShell `Get-AuthenticodeSignature` to report `Valid` before exposing the signed workflow artifact.
8. The signing workflow must not create a GitHub release, publish to `auratism.com`, update a release channel, or distribute the model runtime. Publication is a separate, explicitly authorized operation outside this repository workflow.

The provider project, policy, artifact configuration, trusted-origin rule, certificate, and GitHub Environment are prerequisites, not repository-created resources. They must be configured and independently verified during onboarding before the first request. The API token should be scoped to submitting requests for this project and rotated or revoked after suspected exposure. Repository workflow changes, ownership changes, or provider-policy changes require maintainer review before the next signing request.

## Two independent signatures

**Authenticode** is the Windows signature applied by the accepted signing provider to `Aurify.exe`. It identifies the certificate publisher and protects the executable's signed bytes. A valid Authenticode signature does not authorize a device, endorse a separate runtime, or by itself select an update.

**Updater manifest signature** is Aurify's application-level RSA3072 signature over canonical stable-channel release metadata. The schema-2 catalog binds each version, publication sequence, release ID, immutable executable path, SHA-256, and exact length, plus catalog validity times. The client authenticates the catalog before offering the newest release and again before activation; installing the newest release never bypasses the accepted sequence watermark. Retained history supports publisher continuity and migration, not user-selected versions or downgrades. Manifest signing is operated by the private release service and is not performed by the public GitHub signing workflow.

Both checks may apply to a production `Aurify.exe`, but they use different keys, formats, operators, and trust decisions. Neither signature covers `Aurify-models.zip`; that separate runtime is verified through its pinned descriptor and hashes.

## Preparing an update publication

`tools/publish-update.mjs` prepares an **offline distribution directory**, not a live release-channel transaction. Do not run it against a directory currently served to clients. Deploy the successfully completed package separately with an authorized atomic promotion.

Build `Aurify.exe` and preserve `Aurify-release.json` as unsigned build provenance. If Authenticode is applied, replace only the executable with the verified signed output from that same build. The publisher reads its final version, hashes the final executable bytes, and derives the release ID from those bytes plus the preserved addon hashes. Unsigned-build hashes are not misrepresented as hashes of a signed executable.

Pass exactly one history mode: `--previous <Aurify-updates.json>` for an established catalog, or `--first-release` for a genuinely empty channel. Keep the previous catalog and its adjacent `Aurify-releases/` directory outside the new build directory because a build clears `dist/`. The publisher authenticates previous history, enforces a strictly newer sequence, rejects duplicate versions/identities, and retains at most 32 releases.

For migration from the legacy channel, `--previous <Aurify-update.json>` requires the exact prior `Aurify.exe` beside that manifest. The publisher verifies its signed hash/length and executable version, retains it in the new catalog, and preserves the legacy publication watermark. Expiration prevents client installation but does not erase historical sequence ownership.

The output contains `Aurify-updates.json`, immutable `Aurify-releases/<releaseId>.exe` files, and a schema-1 `Aurify-update.json` for existing clients. Handled manifest-promotion failures restore previous manifests where possible and do not delete payloads that a committed manifest might reference. An interrupted or unsuccessful preparation must never be deployed.
