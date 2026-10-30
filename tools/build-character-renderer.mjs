import { createHash } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import {
  access,
  copyFile,
  mkdir,
  readFile,
  readdir,
  rm,
  stat,
  rename,
  writeFile,
} from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";
import { patchExporter } from "../companion/renderer/patch-exporter.mjs";
import { rendererMetadata } from "../companion/renderer/metadata-lock.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const originalHash =
  "5d3a937b07ea9922ae56ad51b137736f26cf6bc3c2886173c6e4a2873f14b57c";
const originalBytes = 335122647;
const archiveName = "portable-wow-export-win-x64-0.2.19.zip";
const originalUrl =
  "https://github.com/Kruithne/wow.export/releases/download/0.2.19/portable-wow-export-win-x64-0.2.19.zip";
const digest = async (file) => {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest("hex");
};
const runtimeSourceInputs = [
  "LICENSE",
  "NOTICE",
  "package.json",
  "package-lock.json",
  "companion/renderer/CHROMIUM-CREDITS.html",
  "companion/renderer/LIBWEBP-LICENSE",
  "companion/renderer/NODE-ADDON-API-LICENSE.md",
  "companion/renderer/NODE-LICENSE",
  "companion/renderer/NWJS-LICENSE",
  "companion/renderer/NWJS-NOTICE.html",
  "companion/renderer/SQUOOSH-LICENSE",
  "companion/renderer/WEBP-WASM-LICENSE.md",
  "companion/renderer/adapter.js",
  "companion/renderer/bootstrap.js",
  "companion/renderer/browser-blob.mjs",
  "companion/renderer/convert.mjs",
  "companion/renderer/metadata-lock.mjs",
  "companion/renderer/patch-exporter.mjs",
  "companion/renderer/worker.cjs",
  "tools/build-character-renderer.mjs",
];
export async function rendererSourceProvenance() {
  return {
    version: "2.0.6",
    inputs: Object.fromEntries(
      await Promise.all(
        runtimeSourceInputs.map(async (path) => [
          path,
          {
            sha256: await digest(join(root, path)),
            bytes: (await stat(join(root, path))).size,
          },
        ]),
      ),
    ),
  };
}
const replaceExactly = (text, from, to) => {
  if (text.split(from).length !== 2)
    throw new Error("Pinned runtime text anchor drift");
  return text.replace(from, to);
};
async function lockedInput(spec) {
  const cache = join(root, ".sources", "renderer-metadata");
  await mkdir(cache, { recursive: true });
  const target = join(cache, `${spec.sha256}-${spec.name}`);
  const cached = await stat(target).catch((error) => {
    if (error.code === "ENOENT") return null;
    throw error;
  });
  if (
    cached?.isFile() &&
    cached.size === spec.bytes &&
    (await digest(target)) === spec.sha256
  )
    return target;
  await rm(target, { force: true });
  const partial = `${target}.${process.pid}.partial`;
  await rm(partial, { force: true });
  const response = await fetch(spec.url, {
    redirect: "follow",
    headers: { "User-Agent": "Aurify-build/2.0.6" },
  });
  if (!response.ok || response.body === null)
    throw new Error(`Unable to download locked renderer input: ${spec.name}`);
  try {
    await pipeline(
      Readable.fromWeb(response.body),
      createWriteStream(partial, { flags: "wx" }),
    );
    if (
      (await stat(partial)).size !== spec.bytes ||
      (await digest(partial)) !== spec.sha256
    )
      throw new Error(`Renderer input integrity mismatch: ${spec.name}`);
    await rename(partial, target);
  } finally {
    await rm(partial, { force: true });
  }
  return target;
}
async function acquireRendererMetadata() {
  const specs = [
    { ...rendererMetadata.listfile, name: "listfile.csv" },
    { ...rendererMetadata.tactKeys, name: "tact-keys.txt" },
    { ...rendererMetadata.dbd.license, name: "wowdbdefs-license.md" },
    ...rendererMetadata.dbd.entries.map(([table, , bytes, sha256]) => ({
      name: `${table}.dbd`,
      url: `${rendererMetadata.dbd.base}${table}.dbd`,
      bytes,
      sha256,
    })),
  ];
  let cursor = 0;
  const resolved = new Array(specs.length);
  await Promise.all(
    Array.from({ length: 4 }, async () => {
      while (cursor < specs.length) {
        const index = cursor++;
        resolved[index] = await lockedInput(specs[index]);
      }
    }),
  );
  return new Map(specs.map((spec, index) => [spec.name, resolved[index]]));
}
async function filesUnder(base, relative = "") {
  const result = [];
  for (const entry of await readdir(join(base, relative), {
    withFileTypes: true,
  })) {
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isSymbolicLink()) throw new Error("Runtime links are forbidden");
    if (entry.isDirectory()) result.push(...(await filesUnder(base, name)));
    else if (entry.isFile()) result.push(name);
    else throw new Error("Unexpected runtime entry");
  }
  return result.sort();
}
async function findOriginal() {
  if (process.env.AURIFY_RENDERER_ARCHIVE)
    return resolve(process.env.AURIFY_RENDERER_ARCHIVE);
  const work = join(root, ".work");
  for (const entry of (await readdir(work, { withFileTypes: true })).sort(
    (a, b) => a.name.localeCompare(b.name),
  )) {
    if (
      !entry.isDirectory() ||
      !entry.name.startsWith("character-preview-research-")
    )
      continue;
    const candidate = join(work, entry.name, archiveName);
    if (
      await access(candidate).then(
        () => true,
        () => false,
      )
    )
      return candidate;
  }
  throw new Error(
    "Pinned wow.export original is missing; set AURIFY_RENDERER_ARCHIVE to the verified original ZIP",
  );
}
export async function buildCharacterRenderer() {
  if (process.platform !== "win32" || process.arch !== "x64")
    throw new Error("Renderer build requires Windows x64");
  const original = await findOriginal();
  if (
    (await stat(original)).size !== originalBytes ||
    (await digest(original)) !== originalHash
  )
    throw new Error("Pinned wow.export 0.2.19 archive integrity mismatch");
  const threeRoot = join(root, "node_modules", "three");
  if (
    JSON.parse(await readFile(join(threeRoot, "package.json"), "utf8"))
      .version !== "0.180.0"
  )
    throw new Error("Renderer requires exactly Three 0.180.0");
  const sourceProvenance = await rendererSourceProvenance();
  const metadataInputs = await acquireRendererMetadata();
  const work = join(root, ".work", "character-renderer");
  const payload = join(work, "runtime");
  const dist = join(root, "dist");
  await rm(payload, { recursive: true, force: true });
  await mkdir(payload, { recursive: true });
  await mkdir(dist, { recursive: true });
  const powershell = join(
    process.env.SystemRoot || process.env.WINDIR,
    "System32",
    "WindowsPowerShell",
    "v1.0",
    "powershell.exe",
  );
  const ps = (command, env) =>
    execFileSync(
      powershell,
      [
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem; " +
          command,
      ],
      {
        cwd: root,
        windowsHide: true,
        stdio: "pipe",
        env: { ...process.env, ...env },
      },
    );
  // The archive is pinned above. Still reject traversal, links and duplicate Windows paths before extraction.
  ps(
    "$z=[IO.Compression.ZipFile]::OpenRead($env:SOURCE); try {$seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase); foreach($e in $z.Entries){$n=$e.FullName; if($n -match '(^/|\\\\|:|(^|/)\\.\\.?(/|$))' -or -not $seen.Add($n) -or (($e.ExternalAttributes -shr 16) -band 61440) -eq 40960){throw 'Unsafe original ZIP entry'}}; [IO.Compression.ZipFileExtensions]::ExtractToDirectory($z,$env:TARGET)} finally {$z.Dispose()}",
    { SOURCE: original, TARGET: payload },
  );
  const source = join(root, "companion", "renderer");
  const upstream = await readFile(join(payload, "src", "app.js"), "utf8");
  const adapter = await readFile(join(source, "adapter.js"), "utf8");
  const bootstrap = await readFile(join(source, "bootstrap.js"), "utf8");
  await writeFile(
    join(payload, "src", "app.js"),
    patchExporter(upstream, adapter, bootstrap),
  );
  // No updater/collector entry point exists in the generated runtime.
  await rm(join(payload, "updater.exe"), { force: true });
  await rm(join(payload, "src", "js", "workers", "cache-collector.js"), {
    force: true,
  });
  // The runtime is headless and does not use the upstream Blender importer.
  await rm(join(payload, "addon"), { recursive: true, force: true });
  const runtimeSource = join(payload, "src");
  const dbdTarget = join(runtimeSource, "aurify-dbd");
  await mkdir(dbdTarget, { recursive: true });
  for (const [table] of rendererMetadata.dbd.entries)
    await copyFile(
      metadataInputs.get(`${table}.dbd`),
      join(dbdTarget, `${table}.dbd`),
    );
  await copyFile(
    metadataInputs.get("listfile.csv"),
    join(runtimeSource, "aurify-listfile-withcapitals.csv"),
  );
  const tactRing = new Map();
  for (const line of (
    await readFile(metadataInputs.get("tact-keys.txt"), "utf8")
  ).split(/\r?\n/)) {
    if (line.length === 0) continue;
    const match = /^([A-F0-9]{16}) ([A-F0-9]{32})$/.exec(line);
    if (!match) throw new Error("Pinned TACT key input is malformed");
    const name = match[1].toLowerCase();
    const key = match[2].toLowerCase();
    if (tactRing.has(name) && tactRing.get(name) !== key)
      throw new Error("Pinned TACT key input contains a conflict");
    tactRing.set(name, key);
  }
  await writeFile(
    join(runtimeSource, "aurify-tact-keys.json"),
    JSON.stringify(
      Object.fromEntries(
        Array.from(tactRing.entries()).sort(([left], [right]) =>
          left.localeCompare(right),
        ),
      ),
    ) + "\n",
  );
  const licenseDir = join(payload, "license");
  await rm(join(licenseDir, "nwjs.html"), { force: true });
  await copyFile(
    join(source, "NWJS-LICENSE"),
    join(licenseDir, "NWJS-LICENSE"),
  );
  await copyFile(
    join(source, "NWJS-NOTICE.html"),
    join(licenseDir, "NWJS-NOTICE.html"),
  );
  for (const name of [
    "CHROMIUM-CREDITS.html",
    "LIBWEBP-LICENSE",
    "NODE-ADDON-API-LICENSE.md",
    "NODE-LICENSE",
    "SQUOOSH-LICENSE",
    "WEBP-WASM-LICENSE.md",
  ])
    await copyFile(join(source, name), join(licenseDir, name));
  await copyFile(
    metadataInputs.get("wowdbdefs-license.md"),
    join(licenseDir, "WOWDBDEFS-LICENSE.md"),
  );
  await copyFile(join(root, "LICENSE"), join(licenseDir, "AURIFY-LICENSE"));
  await copyFile(join(root, "NOTICE"), join(licenseDir, "AURIFY-NOTICE"));
  const legalPath = join(licenseDir, "LEGAL");
  await writeFile(
    legalPath,
    replaceExactly(
      await readFile(legalPath, "utf8"),
      "For third-party licenses for nwjs and dependencies, see nwjs.html.",
      "For NW.js licensing and provenance, see NWJS-LICENSE and NWJS-NOTICE.html.\nThe complete generated third-party credits for the exact bundled Chromium 141.0.7390.55 release are in CHROMIUM-CREDITS.html.\nNode.js 24.9.0 licensing, including its bundled dependencies, is in NODE-LICENSE.\nThe retained mmap.node addon uses node-addon-api 8.5.0; see NODE-ADDON-API-LICENSE.md.\nThe retained webp_node_enc.wasm comes from webp-wasm 1.0.6 and contains Squoosh-derived WebAssembly using libwebp; see WEBP-WASM-LICENSE.md, SQUOOSH-LICENSE, and LIBWEBP-LICENSE. webp-wasm does not identify the exact Squoosh or libwebp source commits used for that binary.\nAurify modifications are licensed under Apache-2.0; see AURIFY-LICENSE and AURIFY-NOTICE.\nBundled WoWDBDefs definitions retain their CC BY-SA 4.0 license; see WOWDBDEFS-LICENSE.md.",
    ),
  );
  const defaultConfigPath = join(runtimeSource, "default_config.jsonc");
  let defaultConfig = await readFile(defaultConfigPath, "utf8");
  defaultConfig = replaceExactly(
    defaultConfig,
    '"enableBinaryListfile": true',
    '"enableBinaryListfile": false',
  );
  defaultConfig = replaceExactly(
    defaultConfig,
    '"cdnFallbackHosts": "cdn.blizzard.com, archive.wow.tools"',
    '"cdnFallbackHosts": ""',
  );
  for (const key of [
    "listfileBinarySource",
    "listfileURL",
    "listfileFallbackURL",
    "dbdURL",
    "dbdFallbackURL",
    "dbdFilenameURL",
    "dbdFilenameFallbackURL",
    "updateURL",
    "tactKeysURL",
    "tactKeysFallbackURL",
    "realmListURL",
    "armoryURL",
  ]) {
    const pattern = new RegExp(`("${key}"\\s*:\\s*)"[^"]*"`, "g");
    if ((defaultConfig.match(pattern) || []).length !== 1)
      throw new Error(`Pinned runtime configuration anchor drift: ${key}`);
    defaultConfig = defaultConfig.replace(pattern, "$1null");
  }
  await writeFile(defaultConfigPath, defaultConfig);
  for (const name of ["worker.cjs", "convert.mjs", "browser-blob.mjs"])
    await copyFile(join(source, name), join(payload, "src", name));
  const vendor = join(payload, "src", "vendor");
  await mkdir(vendor, { recursive: true });
  const vendorFiles = {
    "build/three.module.js": "three.module.js",
    "build/three.core.js": "three.core.js",
    "examples/jsm/loaders/OBJLoader.js": "OBJLoader.js",
    "examples/jsm/loaders/MTLLoader.js": "MTLLoader.js",
    "examples/jsm/exporters/GLTFExporter.js": "GLTFExporter.js",
  };
  for (const [from, to] of Object.entries(vendorFiles)) {
    let text = await readFile(join(threeRoot, from), "utf8");
    text = text
      .replaceAll("from 'three'", "from './three.module.js'")
      .replaceAll("from '../utils/", "from './");
    if (to === "GLTFExporter.js") {
      // PNG views exclude alignment bytes; the next BIN offset stays padded.
      const imageView =
        "byteLength: buffer.byteLength\n\t\t\t\t};\n\n\t\t\t\twriter.byteOffset += buffer.byteLength;";
      const imageAt = text.indexOf(imageView);
      if (
        imageAt < 0 ||
        text.indexOf(imageView, imageAt + imageView.length) !== -1
      )
        throw new Error("Pinned GLTF image buffer-view anchor drift");
      text = text.replace(
        imageView,
        imageView.replace(
          "byteLength: buffer.byteLength",
          "byteLength: reader.result.byteLength",
        ),
      );
      // Node ESM supplies a different Blob brand than Chromium's FileReader accepts.
      text =
        "import { BrowserBlob as Blob } from '../browser-blob.mjs';\n" + text;
    }
    await writeFile(join(vendor, to), text);
  }
  await copyFile(
    join(threeRoot, "LICENSE"),
    join(payload, "license", "THREE-LICENSE"),
  );
  const manifestPath = join(payload, "package.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  if (manifest.version !== "0.2.19" || manifest.main !== "./src/index.html")
    throw new Error("Upstream manifest drift");
  manifest.name = "aurify-character-renderer";
  manifest.description =
    "Headless Aurify character renderer derived from wow.export 0.2.19";
  manifest.license = "Apache-2.0";
  manifest.product_string = "Aurify model renderer";
  manifest["user-agent"] = "Aurify model renderer (%ver); %osinfo";
  manifest.window = {
    ...manifest.window,
    id: "aurify-model-renderer",
    show: false,
    show_in_taskbar: false,
  };
  manifest["chromium-args"] +=
    " --enable-features=NWESM,NWChainImportNode --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  await writeFile(
    join(payload, "aurify-provenance.json"),
    JSON.stringify(
      {
        schemaVersion: 1,
        version: "0.2.19-aurify.1",
        upstream: {
          url: originalUrl,
          sha256: originalHash,
          bytes: originalBytes,
        },
        aurifySource: sourceProvenance,
        three: "0.180.0",
        retainedBinaries: {
          mmap: {
            path: "mmap.node",
            sha256: await digest(join(payload, "mmap.node")),
            source: "wow.export 0.2.19 node_addons/mmap",
            nodeAddonApi: "8.5.0",
          },
          webpEncoder: {
            path: "src/webp_node_enc.wasm",
            sha256: await digest(join(payload, "src", "webp_node_enc.wasm")),
            package: "webp-wasm 1.0.6",
            npmIntegrity:
              "sha512-RqBXDs/pwLc4dCChFfOw9FvBULRW6o/XGKMSbkWaPuTVtQ0oQx12A/k0rtiKF9fSsb9dxzOFGT7kE2R3jxMszA==",
          },
        },
        metadata: {
          runtimeNetwork:
            "Disabled. Listfile, TACT keys and required DBD definitions are bundled, hash-pinned build inputs; game assets are read only from the exact local client build",
          listfile: {
            source: rendererMetadata.listfile.source,
            release: rendererMetadata.listfile.release,
            sha256: rendererMetadata.listfile.sha256,
            bytes: rendererMetadata.listfile.bytes,
          },
          tactKeys: {
            source: rendererMetadata.tactKeys.source,
            commit: rendererMetadata.tactKeys.commit,
            sha256: rendererMetadata.tactKeys.sha256,
            bytes: rendererMetadata.tactKeys.bytes,
          },
          dbd: {
            source: rendererMetadata.dbd.source,
            commit: rendererMetadata.dbd.commit,
            definitions: rendererMetadata.dbd.entries.length,
          },
        },
        licenses: {
          aurify: "Apache-2.0",
          wowExport: "MIT",
          nwjs: "MIT",
          wowDBDefs: "CC-BY-SA-4.0",
          nodejs: "See NODE-LICENSE",
          nodeAddonApi: "MIT",
          libwebp: "BSD-3-Clause",
          webpWasm: "MIT",
          squoosh: "Apache-2.0",
        },
        format: "corrected-posed-OBJ-to-static-GLB",
        localSource:
          "Exact requested product/version/BuildKey and newest CASC v2 index generation per bucket. No retired-journal merge, remote fallback or unused CDN archive configuration. Required local BuildKey configuration, encoding/root data, mappings and selected primary assets remain mandatory",
        dataCompleteness:
          "Strict ordinary BLTE/DB2 reads; CreatureDisplayInfo/CreatureModelData and Item/ItemSparse/ItemAppearance/ItemDisplayInfo use exact required primary keys with complete global metadata and complete required sections, excluding unavailable unrelated sections without caching partial files. Best-effort relation reads are limited to ItemModifiedAppearance and ItemDisplayInfoModelMatRes: complete validated global metadata and complete readable record/relationship/copy/index sections only; unreadable sections are skipped wholesale, never decoded from zero-filled bytes. Equipment keys derive only from observed visible item IDs with concrete readable mappings; all other modifier/material/resource/helmet/component mappings remain whole-table strict. Explicit empty visible loadouts require no item rows; optional public TACT refresh never weakens required-asset checks",
        presentation: {
          quality: "best-effort",
          warning:
            "Incomplete/unverified preview, not an exact in-game appearance or game capture. Uses only explicitly confirmed appearance and observed gear with readable required mappings, geometry and primary textures from the exact client build; no sample/preset characters, invented gear or placeholder meshes",
          mode: "MELEE",
          nonRenderedSlots: [2, 11, 12, 13, 14, 18],
          rangedSlot18:
            "Not displayed in the static melee presentation; occupied ranged/relic slots do not block rendering",
          guildTabard:
            "Five explicit member-confirmed sparse DB2 values; six exact tier/component texture FDIDs required",
          customization:
            "Geosets, composed textures, uniquely resolved conditional model swaps, complete skinned customization meshes and explicitly selected exact-model BONE corrections; unsupported DisplayInfo/unknown effects, ambiguous correction owners and nonordinary bone inheritance rejected",
          boneCorrections:
            "Independently implemented version-1 BIDA/BOMT parsing and renderer-basis affine correction: q=A*p; local=T(q)*C*T(-q)*A; world=correctedParent*local. Full-detail regular skin 0 with all-bone pose traversal permits transformed plus 0x10000/0x20000/0x40000 LOD metadata; other correction-chain flag semantics remain rejected (wowdev.wiki/M2#LDV1 and #Bones). Mathematical reference wowmodelviewer/wowmodelviewer@678071f0f9156fa0daa5e6bd0ef1426c2255cb01:docs/fbx-customization.md; public format wowdev.wiki/BONE. Independent-renderer agreement, not proprietary-client capture; no copied GPL implementation or neutral/missing-file substitute",
          textureSelection:
            "OBJ export consumes exact validated primary M2 texture-index sets for the body and each equipment renderer. Missing unused or intentionally omitted secondary maps do not block a preview; every selected primary must successfully load, decode and export. Failed eager loads remain absent from the real texture map; no default texture enters the artifact",
          materials:
            "Sampled stand-pose tint, opacity, UV1/UV2 transforms, texture wrapping, culling and supported depth semantics; standard lit or KHR_materials_unlit only. A single two-texture unit using Diffuse_T1_T2 or Diffuse_T1_T1 with Combiners_Mod_Add or Combiners_Mod_AddNA is approximated as the ordinary Combiners_Mod primary diffuse layer: actual selected texture1 with UV0/matrix0, without secondary specular or secondary alpha-sum. A single two-texture Diffuse_T1_Env (VS3)/Combiners_Opaque_AddAlpha (PS13) unit is approximated as Combiners_Opaque: actual selected texture1 with UV0/matrix0 and opaque texture-alpha coverage, omitting secondary reflection/specular; the secondary alpha is reflectivity, not coverage. Body-only single-texture Diffuse_Env/Combiners_Mod with flags16/blend2 retains its real geometry and primary texture using spherical reflection UVs frozen at wow.export 0.2.19's character reference camera (position 0,1.609,2.347; target 0,1.247,0.537), with the pinned shader's weighted-bone/model/view inverse-transpose normals; singular/nonfinite projections fail without fallback. Reflection does not track interactive orbit and is approximate, not equivalent shader output. Other unsupported combiners, unknown/ambiguous primary texture or UV mappings, other view-dependent primary shaders, multipass and unsupported depth semantics are rejected",
        },
      },
      null,
      2,
    ) + "\n",
  );
  const files = [];
  for (const path of await filesUnder(payload))
    files.push({
      path,
      sha256: await digest(join(payload, path)),
      bytes: (await stat(join(payload, path))).size,
    });
  const zipPath = join(dist, "Aurify-models.zip");
  await rm(zipPath, { force: true });
  const entryList = join(work, "entries.json");
  await writeFile(entryList, JSON.stringify(files.map((file) => file.path)));
  ps(
    "$z=[IO.Compression.ZipFile]::Open($env:ZIP,[IO.Compression.ZipArchiveMode]::Create); try {foreach($n in (Get-Content -Raw -LiteralPath $env:ENTRIES|ConvertFrom-Json)) {$e=$z.CreateEntry($n,[IO.Compression.CompressionLevel]::Optimal); $e.LastWriteTime=[DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero); $input=[IO.File]::OpenRead((Join-Path $env:TARGET $n)); try {$output=$e.Open(); try {$input.CopyTo($output)} finally {$output.Dispose()}} finally {$input.Dispose()}}} finally {$z.Dispose()}",
    { ZIP: zipPath, TARGET: payload, ENTRIES: entryList },
  );
  const descriptor = {
    schemaVersion: 1,
    version: "0.2.19-aurify.1",
    sha256: await digest(zipPath),
    bytes: (await stat(zipPath)).size,
    entryPoint: "wow.export.exe",
    source: sourceProvenance,
    files,
  };
  await writeFile(
    join(dist, "Aurify-models.json"),
    JSON.stringify(descriptor, null, 2) + "\n",
  );
  return descriptor;
}
if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  await buildCharacterRenderer();
  console.log("Built dist/Aurify-models.zip and dist/Aurify-models.json");
}
