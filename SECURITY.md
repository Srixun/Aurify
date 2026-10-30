# Security policy

## Supported version

Security fixes are provided for the current Aurify 2.0.6 release line. Older builds should be upgraded before a report is reproduced.

## Reporting a vulnerability

Do not disclose an unpatched vulnerability in a public issue. When this repository is hosted on GitHub, use GitHub's private vulnerability reporting/security-advisory feature. If that feature is unavailable, contact the maintainer, [@Srixun](https://github.com/Srixun), privately and include:

- the affected version and component;
- reproduction steps and expected impact;
- relevant sanitized logs or proof of concept; and
- whether the issue is already public or under active exploitation.

Do not include credentials, device tokens, private player information, production packet captures, or third-party personal data. No response or remediation deadline is promised, but reports will be triaged according to severity and reproducibility.

## Security boundaries

The production authorization service remains private at `auratism.com`. Every protected request is authorized by that server. Device credentials, local validation, UI state, and other client-side checks are not a trust boundary. Discord membership/role authorization may be cached for no more than five minutes; device revocation is checked by the server.

Only `Aurify.exe` is eligible for the project's Authenticode signing process. Its embedded descriptor pins the separately downloaded wow.export-derived model runtime by exact length, archive hash, and per-file hashes before execution; the runtime ZIP itself remains unsigned upstream-derived open-source software. Runtime listfile, TACT-key, and required DBD-definition snapshots are immutable build inputs and are not refreshed from third parties during model work.

Authenticode on `Aurify.exe` and the updater's signed release manifest are independent controls. See [CODE_SIGNING.md](CODE_SIGNING.md).
