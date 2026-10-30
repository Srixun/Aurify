# Contributing to Aurify

Contributions to the client are welcome. Keep changes within the public client boundary: the private Auratism backend, production configuration, credentials, databases, player data, generated binaries, and generated model runtime do not belong in this repository.

## Before opening a pull request

1. Base the change on the current public source and keep it focused.
2. Do not commit `dist/`, `.work/`, `.sources/`, `Aurify.exe`, `Aurify-models.zip`, credentials, logs, packet captures, WoW data, or private user/player information. Never publish a ZIP of a populated working directory; publish only reviewed Git-tracked source.
3. Install dependencies with `npm ci` and run `npm run check` and `npm test`.
4. For build changes, use Windows x64, the exact .NET SDK selected by `global.json`, and the exact hash-pinned wow.export archive documented in [README.md](README.md), then run `npm run build` followed by `npm run check` so generated artifacts and recorded toolchain fingerprints are validated.
5. Update user-facing policy only when behavior actually changes. Do not describe client checks as an authorization boundary or claim SignPath acceptance/signature status without verified provider evidence.

Pull requests require review by the code owner, [@Srixun](https://github.com/Srixun). By intentionally submitting a contribution, you agree that it is provided under the repository's [Apache License 2.0](LICENSE), consistent with section 5 of that license.

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
