# Third-party notices

Aurify is licensed under Apache-2.0. The following components and data retain their own licenses.

## wow.export 0.2.19

Aurify's character-model runtime is derived from wow.export 0.2.19:

- project: <https://github.com/Kruithne/wow.export>
- pinned archive: <https://github.com/Kruithne/wow.export/releases/download/0.2.19/portable-wow-export-win-x64-0.2.19.zip>
- archive size: `335122647` bytes
- SHA-256: `5d3a937b07ea9922ae56ad51b137736f26cf6bc3c2886173c6e4a2873f14b57c`
- license: MIT

Copyright (c) Kruithne <kruithne@gmail.com>
Copyright (c) Marlamin <marlamin@marlamin.com>

The generated runtime is a separately downloaded, hash-pinned, upstream-derived open-source component. The builder removes the unused GPL Blender importer, removes the upstream sample `nwjs.html`, and adds explicit Aurify, NW.js, Chromium, Node.js, node-addon-api, webp-wasm, Squoosh, libwebp, Three, and WoWDBDefs license/notice files. It is not part of `Aurify.exe`, is not independently Authenticode-signed by Aurify, and must not be submitted to SignPath.

## NW.js 0.104.1

- project: <https://github.com/nwjs/nw.js>
- release: <https://nwjs.io/blog/v0.104.1/>
- included versions: NW.js 0.104.1, Chromium 141.0.7390.55, Node.js 24.9.0
- license: MIT

The generated runtime includes `NWJS-LICENSE`, `NWJS-NOTICE.html`, `NODE-LICENSE`, and `CHROMIUM-CREDITS.html`. `NODE-LICENSE` is the complete license bundle from the exact Node.js `v24.9.0` source tag. The credits file contains the complete `chrome://credits` output generated from the official Chrome for Testing 141.0.7390.55 Windows x64 archive, matching the Chromium version bundled by NW.js. The source archive has SHA-256 `ae4c83dfa84a99083ad1eab1f5d76ef214b24443149106eeecb9c0fb3d8b9123`; the generated credits file has SHA-256 `09457e2b31102be34d58ee32ce26a75749d98c7159f2b2a2397f589f86149fc4`. The official NW.js archive's `credits.html` is a sample placeholder and is not used as the dependency inventory.

## Retained renderer binaries

The separately generated runtime retains two binary dependencies from the pinned wow.export archive:

- `mmap.node`: SHA-256 `edf60a5cf562efe42f2f9bdd334c3f0121449d879d972a213b0d0d978a13ad8c`; built from wow.export's MIT-licensed `node_addons/mmap` source using node-addon-api 8.5.0. The exact node-addon-api `v8.5.0` MIT license is included as `NODE-ADDON-API-LICENSE.md`.
- `webp_node_enc.wasm`: SHA-256 `86ad63e3a82b3011a55779264042152bc37c99360ef9140dd8eed543b35c080a`; supplied by `webp-wasm` 1.0.6. The exact npm package is pinned in `bun.lock` by integrity `sha512-RqBXDs/pwLc4dCChFfOw9FvBULRW6o/XGKMSbkWaPuTVtQ0oQx12A/k0rtiKF9fSsb9dxzOFGT7kE2R3jxMszA==`. The package's MIT license is included as `WEBP-WASM-LICENSE.md`.

webp-wasm states that its WebAssembly components were copied from GoogleChromeLabs/Squoosh, whose WebP encoder build uses libwebp. The package does not record the exact Squoosh or libwebp source commits used to produce its 1.0.6 binary. Squoosh's Apache-2.0 license, reviewed at commit `e8d35e0fb66eb16eff6fe8fc773eabcbb7128de3`, is included as `SQUOOSH-LICENSE`. The official libwebp BSD-3-Clause `COPYING` file, reviewed at revision `d2e245ea9e959a5a79e1db0ed2085206947e98f2` that the reviewed Squoosh commit pins in `codecs/webp/Makefile`, is included as `LIBWEBP-LICENSE`. Sources: [Squoosh's pinned WebP build](https://github.com/GoogleChromeLabs/squoosh/blob/e8d35e0fb66eb16eff6fe8fc773eabcbb7128de3/codecs/webp/Makefile) and [libwebp's official `COPYING` file](https://github.com/webmproject/libwebp/blob/d2e245ea9e959a5a79e1db0ed2085206947e98f2/COPYING). Those reviewed revisions establish the distributed license texts only; they do not identify the source revisions used by webp-wasm. The retained binary's reproducible identity is therefore the npm package version, npm integrity, and binary SHA-256 above; no stronger source-commit claim is made.

## WoWDBDefs

- project: <https://github.com/wowdev/WoWDBDefs>
- pinned commit: `835e08f21237a43b23202189363e5c7b6746279c`
- use: 27 required DBD definitions in the separately generated model runtime
- data license: Creative Commons Attribution-ShareAlike 4.0 (`CC-BY-SA-4.0`)

The definitions are copied without semantic modification. The generated runtime includes the pinned repository `LICENSE.md` as `WOWDBDEFS-LICENSE.md`, preserves attribution and source/commit provenance, and applies no additional restriction to those definition files.

## wow-listfile and TACTKeys data

- wow-listfile project: <https://github.com/wowdev/wow-listfile>
- pinned release: `202610291858`, `verified-listfile-withcapitals.csv`
- TACTKeys project: <https://github.com/wowdev/TACTKeys>
- pinned commit: `e435c8b7af90e2d4d15e065eebbd7dc63957571b`

The builder verifies both public data snapshots by exact byte length and SHA-256 from `companion/renderer/metadata-lock.mjs`, then bundles them into the separately generated runtime. The model worker does not refresh them over the network. Neither project published a repository-level license file at the reviewed pins; this project makes no broader licensing claim about those factual path/key records, and they are not included in `Aurify.exe` or the public source inventory.

## Three 0.180.0

- project: <https://github.com/mrdoob/three.js>
- package: <https://www.npmjs.com/package/three/v/0.180.0>
- license: MIT

Copyright © 2010-2025 three.js authors

Selected Three modules are copied into the separately generated character-model runtime. The runtime includes `THREE-LICENSE`.

## luaparse 0.3.1

- project: <https://github.com/fstirlitz/luaparse>
- package: <https://www.npmjs.com/package/luaparse/v/0.3.1>
- license: MIT

Copyright (c) Oskar Schöldström 2012-2014

## Prettier 3.6.2

- project: <https://github.com/prettier/prettier>
- package: <https://www.npmjs.com/package/prettier/v/3.6.2>
- license: MIT
- use: development tooling only; not included in `Aurify.exe`

Copyright © James Long and contributors

## MIT license text

The MIT license applies to the components identified above.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Unicode CLDR windowsZones-derived mapping

`companion/native/ForeverTimeZone.cs` contains a Windows-to-IANA time-zone mapping derived from the Unicode Common Locale Data Repository `windowsZones.xml` territory `001` rows (CLDR main data, `typeVersion="2021a"`), pinned to commit `7afa5cdb6b7fe7ae65f934c6dbaa374f25b5a616`:

<https://github.com/unicode-org/cldr/blob/7afa5cdb6b7fe7ae65f934c6dbaa374f25b5a616/common/supplemental/windowsZones.xml>

The exact UTF-8 source file is 49,943 bytes with SHA-256 `9d6e53736534f6998f35191deb6e0060ba338bdb293bb3b0753cddc4a586b50f`. All 142 territory `001` mappings and first IANA types match the compiled table.

The mapping is licensed under Unicode License v3 (`Unicode-3.0`). The notice is embedded in the distributed executable as `Aurify.Unicode.License`.

Copyright © 2001–2026 Unicode, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of data files and any associated documentation (the "Data Files") or software
and any associated documentation (the "Software") to deal in the Data Files
or Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, and/or sell copies of the Data
Files or Software, and to permit persons to whom the Data Files or Software
are furnished to do so, provided that either (a) this copyright and permission
notice appear with all copies of the Data Files or Software, or (b) this
copyright and permission notice appear in associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF THIRD
PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE
LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR
ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN
AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR
IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall not
be used in advertising or otherwise to promote the sale, use or other dealings
in these Data Files or Software without prior written authorization of the
copyright holder.

SPDX-License-Identifier: Unicode-3.0

## Signing scope

Only `Aurify.exe` is submitted to SignPath. Its embedded model-runtime descriptor is covered as part of that executable. The separately downloaded runtime archive, generated `Aurify-models.zip`, standalone descriptor, and third-party development packages are not independently signed or distributed through the Aurify SignPath workflow.
