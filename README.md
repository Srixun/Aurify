# Aurify

Aurify 2.0.6 is a Windows companion for WoW Forever. It installs a per-user native companion and addon, discovers or accepts a supported game folder, and can synchronize selected addon snapshots, combat-log data, and generated character-model previews.

This repository is the client-only public source. The production authorization service and website at `auratism.com` are private and are not included. The server authorizes every protected request; client-side checks are defense in depth, not a trust boundary.

## Product behavior

- Auratism uploads do not begin until the user selects **Pair with Discord** and explicitly approves the displayed device code in the browser.
- Each protected request is authorized by the private server. Discord guild-role results may be cached for no more than five minutes. A device can be revoked from the Auratism members page; revoked devices are rejected by the server.
- Optional ForeverLogs support is separate. The user must select **Connect ForeverLogs** and approve upload access in the browser before Aurify sends combat logs there. Future reports default to private unless the user explicitly confirms public reports.
- Character-model generation uses a separately downloaded, hash-pinned runtime derived from wow.export 0.2.19. The runtime bundles immutable, hash-verified listfile, TACT-key, and required DBD-definition snapshots, so model work does not refresh third-party metadata over the network. It is not part of `Aurify.exe` and is not independently Authenticode-signed by Aurify.
- Opening `Aurify.exe` shows a first-install disclosure with **Install** and **Cancel**. Only **Install** copies the managed companion, creates Desktop, Startup, and uninstall shortcuts, and starts the tray process. Use **Uninstall Aurify** to remove the managed app and automatic startup. Uninstall intentionally keeps pairing, settings, checkpoints, game addons, and changed or unrecognized files.
- After pairing, **Check for updates** retrieves an authenticated stable catalog without downloading an executable. It shows only the newest authenticated stable release, including its version and exact byte size. Press **Install latest update** to explicitly download that release; closing or hiding the window does not authorize an update.
- Retained signed catalog history and immutable release paths support publisher continuity and migration only; they are not a user-facing version selector or downgrade mechanism. Already accepted releases cannot be replayed or downgraded. Verification and transactional activation preserve the prior installation if an update fails.

See [PRIVACY.md](PRIVACY.md) for data destinations and controls, and [SECURITY.md](SECURITY.md) for the security policy.

## Source and build

Requirements:

- Windows x64;
- Node.js 24 or newer;
- the exact .NET SDK `8.0.427` selected by `global.json`; and
- the supported Windows x64 .NET Framework runtime assemblies used as explicit compiler references.

Install the locked dependencies:

```powershell
npm ci
```

Download the exact upstream archive below and set `AURIFY_RENDERER_ARCHIVE` to its local path:

- URL: `https://github.com/Kruithne/wow.export/releases/download/0.2.19/portable-wow-export-win-x64-0.2.19.zip`
- Size: `335122647` bytes
- SHA-256: `5d3a937b07ea9922ae56ad51b137736f26cf6bc3c2886173c6e4a2873f14b57c`

The build checks both the byte length and SHA-256 before extraction.
The build also downloads immutable public renderer metadata inputs pinned in `companion/renderer/metadata-lock.mjs`. Every input is verified by byte length and SHA-256 before it enters the generated runtime; these downloads occur only while building, not during model work.

The generated runtime's `license/` directory carries the complete Node.js 24.9.0 license bundle, the exact retained node-addon-api and webp-wasm licenses, the Squoosh Apache-2.0 license, and the Chromium dependency credits described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Native release compilation uses the SDK-pinned Roslyn compiler with deterministic output, fixed payload timestamps, stable path mapping, explicit framework references, and recorded SHA-256/length fingerprints for the .NET host, compiler, and every referenced assembly. The post-build check verifies those fingerprints against the current build machine.

```powershell
$env:AURIFY_RENDERER_ARCHIVE = 'C:\path\to\portable-wow-export-win-x64-0.2.19.zip'
npm run build
npm run check
npm test
npm run format:check
```

The build produces `dist/Aurify.exe` and local model-runtime outputs. Generated `dist/`, `.work/`, `.sources/`, `Aurify.exe`, and `Aurify-models.zip` are not source and must not be committed or copied into a source release. Publish only Git-tracked files from a reviewed commit (for example, GitHub's source archive), never a ZIP of the working directory.

Before installing dependencies or building an extracted source archive, validate its exact source-only inventory from a checkout:

```powershell
node tools/check.mjs --source-only 'C:\path\to\extracted-source'
```

This mode rejects generated/private roots, unexpected files, missing source, and symbolic links. Normal post-build checks permit local generated outputs but require the Git-tracked publication inventory to match the client-only allowlist.

## Code signing policy

The complete policy is in [CODE_SIGNING.md](CODE_SIGNING.md). Only `Aurify.exe` is submitted for Authenticode signing. Its embedded model-runtime descriptor is therefore covered as part of the executable; the separate runtime ZIP remains unsigned and hash-verified. Authenticode and Aurify's independently verified updater-manifest signature serve different purposes.

SignPath/SignPath Foundation onboarding is pending. This project does not claim provider acceptance, certificate issuance, or that any current executable is signed. **Only if the provider accepts the project and its certificate is used, the applicable attribution is: “Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).”**

## Contributing and license

See [CONTRIBUTING.md](CONTRIBUTING.md) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Aurify is licensed under the [Apache License 2.0](LICENSE).
