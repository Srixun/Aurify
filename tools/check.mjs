import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readdir, readFile, stat } from "node:fs/promises";
import { dirname, extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { rendererMetadata } from "../companion/renderer/metadata-lock.mjs";
import {
  dotnetSdkVersion,
  resolveDotnetToolchain,
} from "./dotnet-toolchain.mjs";
import { rendererSourceProvenance } from "./build-character-renderer.mjs";

const sourceOnly = process.argv[2] === "--source-only";
if (process.argv.length > (sourceOnly ? 4 : 2))
  throw new Error(
    "Usage: node tools/check.mjs [--source-only <exported-tree>]",
  );
const root = sourceOnly
  ? resolve(process.argv[3] || ".")
  : resolve(dirname(fileURLToPath(import.meta.url)), "..");
const expectedFiles = [
  ".github/CODEOWNERS",
  ".github/dependabot.yml",
  ".github/workflows/ci.yml",
  ".github/workflows/codeql.yml",
  ".github/workflows/release-signing.yml",
  ".gitattributes",
  ".gitignore",
  "global.json",
  "CODE_SIGNING.md",
  "CONTRIBUTING.md",
  "LICENSE",
  "NOTICE",
  "PRIVACY.md",
  "README.md",
  "SECURITY.md",
  "THIRD_PARTY_NOTICES.md",
  "companion/addon/Aurify/Aurify.lua",
  "companion/addon/Aurify/Aurify.toc",
  "companion/app.manifest",
  "companion/branding/aurify.ico",
  "companion/launcher.cs",
  "companion/native/Activity.cs",
  "companion/native/AddonUpdates.cs",
  "companion/native/AddonVersions.cs",
  "companion/native/Addons.cs",
  "companion/native/CharacterModels.cs",
  "companion/native/Characters.cs",
  "companion/native/Combat.cs",
  "companion/native/Contracts.cs",
  "companion/native/Core.cs",
  "companion/native/ForeverClient.cs",
  "companion/native/ForeverSync.cs",
  "companion/native/ForeverTimeZone.cs",
  "companion/native/Game.cs",
  "companion/native/Host.cs",
  "companion/native/LuaData.cs",
  "companion/native/ModelRuntime.cs",
  "companion/native/Overlord.cs",
  "companion/native/Professions.cs",
  "companion/native/Snapshots.cs",
  "companion/native/Update.cs",
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
  "companion/third-party/UNICODE-LICENSE",
  "companion/update-public-key.xml",
  "package-lock.json",
  "package.json",
  "tests/addon-updates-native.test.mjs",
  "tests/addons.test.mjs",
  "tests/character-renderer.test.mjs",
  "tests/characters-native.test.mjs",
  "tests/combat-efficiency.test.mjs",
  "tests/forever-client.test.mjs",
  "tests/fixtures/aurify-1.0.0/Aurify.lua.gz",
  "tests/fixtures/aurify-1.0.0/Aurify.toc.gz",
  "tests/host-efficiency.test.mjs",
  "tests/native-bridge.mjs",
  "tests/native/CombatEfficiencyProbe.cs",
  "tests/native/ForeverProbe.cs",
  "tests/native/HostEfficiencyProbe.cs",
  "tests/native/NativeProbe.cs",
  "tests/native/SnapshotEfficiencyProbe.cs",
  "tests/native/SyntheticGame.cs",
  "tests/native/UpdateProbe.cs",
  "tests/publish-update.test.mjs",
  "tests/snapshot-efficiency.test.mjs",
  "tests/updates.test.mjs",
  "tools/build-character-renderer.mjs",
  "tools/build-companion.mjs",
  "tools/build-native-tests.mjs",
  "tools/check.mjs",
  "tools/dotnet-toolchain.mjs",
  "tools/publish-update.mjs",
].sort();
const generatedRoots = new Set([
  ".git",
  ".runtime",
  ".sources",
  ".verification",
  ".work",
  "dist",
  "node_modules",
]);
const repositoryState = await stat(join(root, ".git")).catch((error) => {
  if (error.code === "ENOENT") return null;
  throw error;
});

async function inventory(directory, relative = "") {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (!relative && generatedRoots.has(entry.name)) {
      if (sourceOnly)
        throw new Error(
          `Generated/private root is forbidden in an exported source tree: ${entry.name}`,
        );
      continue;
    }
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isSymbolicLink())
      throw new Error(`Source links are forbidden: ${name}`);
    if (entry.isDirectory())
      files.push(...(await inventory(join(directory, entry.name), name)));
    else if (entry.isFile()) files.push(name);
    else throw new Error(`Unexpected source entry: ${name}`);
  }
  return files;
}

const actualFiles = (await inventory(root)).sort();
if (JSON.stringify(actualFiles) !== JSON.stringify(expectedFiles)) {
  const expected = new Set(expectedFiles);
  const actual = new Set(actualFiles);
  const missing = expectedFiles.filter((file) => !actual.has(file));
  const unexpected = actualFiles.filter((file) => !expected.has(file));
  throw new Error(
    `Public source inventory mismatch; missing: ${missing.join(", ") || "none"}; unexpected: ${unexpected.join(", ") || "none"}`,
  );
}
if (repositoryState !== null) {
  const trackedFiles = execFileSync("git", ["ls-files", "-z"], {
    cwd: root,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  })
    .split("\0")
    .filter(Boolean)
    .sort();
  const forbiddenTracked = trackedFiles.filter((file) =>
    generatedRoots.has(file.split("/")[0]),
  );
  if (forbiddenTracked.length > 0)
    throw new Error(
      `Generated/private roots are tracked: ${forbiddenTracked.join(", ")}`,
    );
  if (JSON.stringify(trackedFiles) !== JSON.stringify(expectedFiles))
    throw new Error(
      "Git-tracked publication inventory must exactly match the public source inventory",
    );
}
if (sourceOnly) {
  console.log(
    "Verified exact exported public source inventory; no generated/private roots.",
  );
  process.exit(0);
}
const { default: luaparse } = await import("luaparse");

const manifest = JSON.parse(await readFile(join(root, "package.json"), "utf8"));
const lock = JSON.parse(
  await readFile(join(root, "package-lock.json"), "utf8"),
);
const globalJson = JSON.parse(
  await readFile(join(root, "global.json"), "utf8"),
);
if (
  manifest.name !== "aurify" ||
  manifest.version !== "2.0.6" ||
  manifest.private !== false ||
  manifest.license !== "Apache-2.0" ||
  manifest.type !== "module" ||
  manifest.engines?.node !== ">=24" ||
  manifest.dependencies?.luaparse !== "0.3.1" ||
  manifest.dependencies?.three !== "0.180.0" ||
  manifest.devDependencies?.prettier !== "3.6.2" ||
  lock.name !== manifest.name ||
  lock.version !== manifest.version ||
  lock.packages?.[""]?.name !== manifest.name ||
  lock.packages?.[""]?.version !== manifest.version ||
  lock.packages?.[""]?.license !== manifest.license ||
  globalJson.sdk?.version !== dotnetSdkVersion ||
  globalJson.sdk?.rollForward !== "disable" ||
  globalJson.sdk?.allowPrerelease !== false
) {
  throw new Error(
    "Package and lock metadata do not match the public release contract",
  );
}
const lockedInputIsValid = (input) =>
  Number.isSafeInteger(input.bytes) &&
  input.bytes > 0 &&
  /^[a-f0-9]{64}$/.test(input.sha256) &&
  /^https:\/\//.test(input.url);
const dbdTables = rendererMetadata.dbd.entries.map((entry) => entry[0]);
if (
  !lockedInputIsValid(rendererMetadata.listfile) ||
  !lockedInputIsValid(rendererMetadata.tactKeys) ||
  !lockedInputIsValid(rendererMetadata.dbd.license) ||
  !rendererMetadata.listfile.url.includes(
    `/releases/download/${rendererMetadata.listfile.release}/`,
  ) ||
  !rendererMetadata.tactKeys.url.includes(rendererMetadata.tactKeys.commit) ||
  !rendererMetadata.dbd.base.includes(rendererMetadata.dbd.commit) ||
  !rendererMetadata.dbd.license.url.includes(rendererMetadata.dbd.commit) ||
  rendererMetadata.dbd.entries.length !== 27 ||
  new Set(dbdTables).size !== dbdTables.length ||
  JSON.stringify(dbdTables) !== JSON.stringify([...dbdTables].sort()) ||
  rendererMetadata.dbd.entries.some(
    ([table, fileDataID, bytes, sha256]) =>
      !/^[A-Za-z0-9]+$/.test(table) ||
      !Number.isSafeInteger(fileDataID) ||
      fileDataID <= 0 ||
      !Number.isSafeInteger(bytes) ||
      bytes <= 0 ||
      !/^[a-f0-9]{64}$/.test(sha256),
  ) ||
  [
    rendererMetadata.listfile.url,
    rendererMetadata.tactKeys.url,
    rendererMetadata.dbd.base,
    rendererMetadata.dbd.license.url,
  ].some((url) => /\/(?:latest|master|refs\/heads)\//i.test(url))
) {
  throw new Error("Renderer metadata lock is mutable or malformed");
}

const javascriptFiles = expectedFiles.filter((file) =>
  [".cjs", ".js", ".mjs"].includes(extname(file)),
);
for (const file of javascriptFiles) {
  execFileSync(process.execPath, ["--check", join(root, file)], {
    cwd: root,
    stdio: "pipe",
    windowsHide: true,
  });
}

const toc = await readFile(
  join(root, "companion", "addon", "Aurify", "Aurify.toc"),
  "utf8",
);
if (
  !/^## Interface:\s*16001\s*$/m.test(toc) ||
  !/^## SavedVariables:\s*AurifyDB\s*$/m.test(toc) ||
  !/^Aurify\.lua\s*$/m.test(toc)
) {
  throw new Error("The Forever-compatible Aurify addon manifest is invalid");
}
luaparse.parse(
  await readFile(
    join(root, "companion", "addon", "Aurify", "Aurify.lua"),
    "utf8",
  ),
  { luaVersion: "5.1" },
);

const allowedOrigins = new Set([
  "http://schemas.microsoft.com",
  "https://auratism.com",
  "https://foreverlogs.gg",
  "https://github.com",
  "https://storage.googleapis.com",
  "https://nwjs.io",
  "https://raw.githubusercontent.com",
  "https://registry.npmjs.org",
  "https://www.curseforge.com",
  "https://www.unicode.org",
]);
const immutableThirdPartyNotices = new Map([
  [
    "companion/renderer/CHROMIUM-CREDITS.html",
    [
      14790335,
      "09457e2b31102be34d58ee32ce26a75749d98c7159f2b2a2397f589f86149fc4",
    ],
  ],
  [
    "companion/renderer/LIBWEBP-LICENSE",
    [1496, "5aec868f669e384a22372a4e8a1a6cd7d44c64cd451f960ca69cc170d1e13acf"],
  ],
  [
    "companion/renderer/NODE-ADDON-API-LICENSE.md",
    [1150, "89024017b88a9f2b763f79b941a4f2db3b4428edfcacdc0b23866b2da633ad0c"],
  ],
  [
    "companion/renderer/NODE-LICENSE",
    [
      143310,
      "537308465103a306d0e3eecf42632b4ff1b48aaaec044e9fc10a78c81fd00b34",
    ],
  ],
  [
    "companion/renderer/SQUOOSH-LICENSE",
    [11357, "c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4"],
  ],
  [
    "companion/renderer/WEBP-WASM-LICENSE.md",
    [1108, "420bacb5e7bd5faa10010d7d3d0bace85f6ac3b3efd3aa6a806453c480638628"],
  ],
]);
const originFiles = expectedFiles.filter(
  (file) =>
    !immutableThirdPartyNotices.has(file) &&
    (file === "package-lock.json" ||
      file.startsWith(".github/workflows/") ||
      file.startsWith("companion/") ||
      (file.startsWith("tools/") && file !== "tools/check.mjs")),
);
const textualExtensions = new Set([
  "",
  ".cjs",
  ".cs",
  ".js",
  ".json",
  ".lua",
  ".html",
  ".manifest",
  ".md",
  ".mjs",
  ".toc",
  ".txt",
  ".yaml",
  ".yml",
  ".xml",
]);
const developerPath =
  /(?:[A-Z]:[\\/](?:AuratismMembers|Program Files|Services|Users)(?:[\\/]|$)|[\\/](?:home|Users)[\\/][^\s"']+)/i;
const secretPatterns = [
  [
    "private key",
    new RegExp(
      ["-----BEGIN ", "(?:RSA |EC |OPENSSH )?", "PRIVATE KEY-----"].join(""),
    ),
  ],
  ["GitHub token", /gh[pousr]_[A-Za-z0-9_]{20,}/],
  ["AWS access key", /AKIA[0-9A-Z]{16}/],
  ["Discord token", /[MN][A-Za-z\d]{23,}\.[\w-]{6}\.[\w-]{27}/],
  ["concrete bearer credential", /Bearer\s+[A-Za-z0-9._~+/-]{32,}/],
  [
    "CurseForge credential",
    /AURA_CURSEFORGE_API_KEY\s*=\s*["']?[A-Za-z0-9]{20,}/,
  ],
];
for (const file of expectedFiles) {
  const immutableNotice = immutableThirdPartyNotices.get(file);
  if (immutableNotice) {
    const notice = await readFile(join(root, file));
    if (
      notice.length !== immutableNotice[0] ||
      createHash("sha256").update(notice).digest("hex") !== immutableNotice[1]
    )
      throw new Error(`Immutable third-party notice drifted: ${file}`);
    continue;
  }
  if (!textualExtensions.has(extname(file))) continue;
  const source = await readFile(join(root, file), "utf8");
  if (developerPath.test(source))
    throw new Error(`Developer-specific or secret filesystem path in ${file}`);
  for (const [label, pattern] of secretPatterns) {
    if (pattern.test(source))
      throw new Error(`Potential ${label} embedded in public source: ${file}`);
  }
}
for (const file of expectedFiles.filter((name) =>
  name.startsWith(".github/workflows/"),
)) {
  const source = await readFile(join(root, file), "utf8");
  if (/^\s*pull_request_target\s*:/m.test(source))
    throw new Error(`Privileged pull_request_target trigger in ${file}`);
  for (const match of source.matchAll(/^\s*uses:\s*([^\s#]+)(?:\s+#.*)?$/gm)) {
    if (!/^[^@]+@[a-f0-9]{40}$/.test(match[1]))
      throw new Error(`GitHub Action is not commit-pinned in ${file}`);
  }
}
for (const file of originFiles) {
  if (!textualExtensions.has(extname(file))) continue;
  const source = await readFile(join(root, file), "utf8");

  const networkSource =
    file === "companion/renderer/patch-exporter.mjs"
      ? source.replace(
          /'((?:SUBMIT|FINALIZE)_URL): "https:\/\/www\.kruithne\.net\/wow\.export\/v2\/cache\/(?:submit|finalize)"',\s*"\1: null"/g,
          '""',
        )
      : source;
  for (const match of networkSource.matchAll(/https?:\/\/[^\s"'`<>()]+/g)) {
    let origin;
    try {
      origin = new URL(match[0]).origin;
    } catch {
      throw new Error(`Malformed external URL in ${file}: ${match[0]}`);
    }
    if (!allowedOrigins.has(origin))
      throw new Error(`Unexpected external origin in ${file}: ${origin}`);
  }
}

const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
const dist = join(root, "dist");
const distState = await stat(dist).catch((error) => {
  if (error.code === "ENOENT") return null;
  throw error;
});
if (distState) {
  if (!distState.isDirectory()) throw new Error("dist must be a directory");
  const artifacts = await readdir(dist, { withFileTypes: true });
  if (artifacts.some((entry) => !entry.isFile()))
    throw new Error("The release directory may contain only release files");
  const names = artifacts.map((entry) => entry.name).sort();
  const expectedArtifacts = [
    "Aurify-models.json",
    "Aurify-models.zip",
    "Aurify-release.json",
    "Aurify.exe",
  ];
  if (names.includes("Aurify-update.json"))
    expectedArtifacts.push("Aurify-update.json");
  expectedArtifacts.sort();
  if (JSON.stringify(names) !== JSON.stringify(expectedArtifacts))
    throw new Error(
      "The release directory contains an obsolete or unexpected artifact",
    );

  const executable = join(dist, "Aurify.exe");
  const native = await readFile(executable);
  const pe = native.length >= 64 ? native.readUInt32LE(0x3c) : -1;
  const optional = pe + 24;
  if (
    native.length < 64 ||
    pe < 0 ||
    optional + 70 > native.length ||
    native.toString("ascii", 0, 2) !== "MZ" ||
    native.toString("ascii", pe, pe + 4) !== "PE\0\0" ||
    ![0x10b, 0x20b].includes(native.readUInt16LE(optional)) ||
    native.readUInt16LE(optional + 68) !== 2
  ) {
    throw new Error("Aurify.exe must be a standalone Windows GUI executable");
  }

  const payloadEntries = [
    "addon/Aurify/Aurify.lua",
    "addon/Aurify/Aurify.toc",
  ].sort();
  const release = JSON.parse(
    await readFile(join(dist, "Aurify-release.json"), "utf8"),
  );
  const releaseFields = [
    "Aurify.exe",
    "addon/Aurify/Aurify.toc",
    "addon/Aurify/Aurify.lua",
  ];
  const hashes = Object.fromEntries(
    await Promise.all(
      releaseFields.map(async (file) => [
        file,
        digest(
          await readFile(
            file === "Aurify.exe" ? executable : join(root, "companion", file),
          ),
        ),
      ]),
    ),
  );
  const releaseId = digest(
    Buffer.from(
      Object.entries(hashes)
        .map(([file, hash]) => `${file}\n${hash}\n`)
        .join(""),
    ),
  );
  const builtToolchain = await resolveDotnetToolchain();
  if (
    release.schema !== 1 ||
    release.product !== "Aurify" ||
    release.version !== "2.0.6" ||
    release.releaseId !== releaseId ||
    release.payloadSha256 !== hashes["Aurify.exe"] ||
    release.payloadLength !== native.length ||
    JSON.stringify(Object.keys(release.hashes ?? {}).sort()) !==
      JSON.stringify([...releaseFields].sort()) ||
    releaseFields.some((file) => release.hashes[file] !== hashes[file]) ||
    JSON.stringify(release.toolchain) !==
      JSON.stringify(builtToolchain.provenance)
  ) {
    throw new Error(
      "Aurify release metadata does not match the built artifacts",
    );
  }

  const modelArchive = await readFile(join(dist, "Aurify-models.zip"));
  const modelDescriptor = JSON.parse(
    await readFile(join(dist, "Aurify-models.json"), "utf8"),
  );
  const modelSource = await rendererSourceProvenance();
  if (
    modelDescriptor.schemaVersion !== 1 ||
    modelDescriptor.version !== "0.2.19-aurify.1" ||
    modelDescriptor.entryPoint !== "wow.export.exe" ||
    modelDescriptor.sha256 !== digest(modelArchive) ||
    modelDescriptor.bytes !== modelArchive.length ||
    !Array.isArray(modelDescriptor.files) ||
    modelDescriptor.files.length === 0 ||
    JSON.stringify(modelDescriptor.source) !== JSON.stringify(modelSource)
  ) {
    throw new Error("The unsigned model runtime descriptor is invalid");
  }

  const windows = process.env.SystemRoot || process.env.WINDIR;
  if (!windows) throw new Error("The Windows directory is unavailable");
  const powershell = join(
    windows,
    "System32",
    "WindowsPowerShell",
    "v1.0",
    "powershell.exe",
  );
  const resourceInspection = String.raw`
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
$assembly=[Reflection.Assembly]::LoadFile($env:AURIFY_CHECK_EXE)
$resources=@($assembly.GetManifestResourceNames() | Sort-Object)
$hashes=@{}
foreach($name in @('Aurify.ModelRuntime','Aurify.Unicode.License','Aurify.Update.PublicKey')){
  $stream=$assembly.GetManifestResourceStream($name)
  if($null -eq $stream){throw "Embedded resource missing: $name"}
  try {$sha=[Security.Cryptography.SHA256]::Create(); try {$hashes[$name]=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}} finally {$stream.Dispose()}
}
$stream=$assembly.GetManifestResourceStream('Aurify.Payload.zip')
if($null -eq $stream){throw 'Embedded payload missing'}
$zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Read,$false)
$entries=@()
try {
  foreach($entry in $zip.Entries){
    $input=$entry.Open(); $sha=[Security.Cryptography.SHA256]::Create()
    try {$hash=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose(); $input.Dispose()}
    $entries+=@{name=$entry.FullName;hash=$hash}
  }
} finally {$zip.Dispose()}
$worker=$assembly.GetManifestResourceStream('Aurify.Uninstall.exe')
if($null -eq $worker){throw 'Native uninstall worker missing'}
try {
  $reader=New-Object IO.BinaryReader($worker)
  if($worker.Length -gt 1048576 -or $worker.Length -lt 64 -or $reader.ReadUInt16() -ne 23117){throw 'Invalid thin uninstall worker'}
  $worker.Position=60; $pe=$reader.ReadUInt32()
  if($pe+94 -gt $worker.Length){throw 'Invalid uninstall PE offset'}
  $worker.Position=$pe; if($reader.ReadUInt32() -ne 17744){throw 'Invalid uninstall PE'}
  $worker.Position=$pe+92; if($reader.ReadUInt16() -ne 2){throw 'Uninstall worker is not GUI subsystem'}
} finally {$worker.Dispose()}
@{resources=$resources;entries=$entries;hashes=$hashes} | ConvertTo-Json -Depth 5 -Compress
`;
  const embedded = JSON.parse(
    execFileSync(
      powershell,
      [
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        resourceInspection,
      ],
      {
        cwd: root,
        env: { ...process.env, AURIFY_CHECK_EXE: executable },
        stdio: "pipe",
        encoding: "utf8",
        windowsHide: true,
      },
    ),
  );
  const expectedResources = [
    "Aurify.ModelRuntime",
    "Aurify.Payload.zip",
    "Aurify.Unicode.License",
    "Aurify.Uninstall.exe",
    "Aurify.Update.PublicKey",
  ].sort();
  if (JSON.stringify(embedded.resources) !== JSON.stringify(expectedResources))
    throw new Error("Aurify.exe embeds an unexpected managed resource set");
  if (
    JSON.stringify(embedded.entries.map((entry) => entry.name).sort()) !==
    JSON.stringify(payloadEntries)
  ) {
    throw new Error("Aurify.exe embeds an unexpected addon payload");
  }
  for (const entry of embedded.entries) {
    if (
      entry.hash !== digest(await readFile(join(root, "companion", entry.name)))
    ) {
      throw new Error(`Embedded addon differs from source: ${entry.name}`);
    }
  }
  const resourceFiles = {
    "Aurify.ModelRuntime": join(dist, "Aurify-models.json"),
    "Aurify.Unicode.License": join(
      root,
      "companion",
      "third-party",
      "UNICODE-LICENSE",
    ),
    "Aurify.Update.PublicKey": join(root, "companion", "update-public-key.xml"),
  };
  for (const [name, file] of Object.entries(resourceFiles)) {
    if (embedded.hashes[name] !== digest(await readFile(file)))
      throw new Error(`Embedded resource differs from source: ${name}`);
  }
}

console.log(
  `Checked ${javascriptFiles.length} JavaScript modules, the Lua 5.1 addon, external origins, and the public source inventory${distState ? ", including native release artifacts" : ""}.`,
);
