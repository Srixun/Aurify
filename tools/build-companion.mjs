import { copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildCharacterRenderer } from "./build-character-renderer.mjs";
import { resolveDotnetToolchain } from "./dotnet-toolchain.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "dist");
const packageRoot = join(root, ".work", "aurify-build", "payload");
const executable = join(dist, "Aurify.exe");
const work = join(root, ".work", "aurify-build");
const payloadArchive = join(work, "Aurify.Payload.zip");
const uninstallWorker = join(work, "Aurify.Uninstall.exe");
if (process.platform !== "win32" || process.arch !== "x64") {
  throw new Error(
    "The Aurify Windows x64 release must be built with Windows x64 Node.js",
  );
}
const toolchain = await resolveDotnetToolchain();
const windows = process.env.SystemRoot || process.env.WINDIR;
await rm(dist, { recursive: true, force: true });
await rm(work, { recursive: true, force: true });
await mkdir(packageRoot, { recursive: true });
await mkdir(work, { recursive: true });
await mkdir(dist, { recursive: true });
// The signed native binary pins this separate runtime; it is never a launcher payload entry.
await buildCharacterRenderer();
const buildEnvironment = { ...process.env, TEMP: work, TMP: work };
for (const name of ["Aurify.toc", "Aurify.lua"]) {
  await mkdir(join(packageRoot, "addon", "Aurify"), { recursive: true });
  await copyFile(
    join(root, "companion", "addon", "Aurify", name),
    join(packageRoot, "addon", "Aurify", name),
  );
}
// Fixed entries: no directory placeholders, shell entry points, or unreviewed addon files.
const entries = ["addon/Aurify/Aurify.toc", "addon/Aurify/Aurify.lua"];
const powershell = join(
  windows,
  "System32",
  "WindowsPowerShell",
  "v1.0",
  "powershell.exe",
);
execFileSync(
  powershell,
  [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[IO.Compression.ZipFile]::Open($env:AURIFY_PAYLOAD_ARCHIVE,[IO.Compression.ZipArchiveMode]::Create); try { foreach($name in ($env:AURIFY_PAYLOAD_ENTRIES|ConvertFrom-Json)){ $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal); $entry.LastWriteTime=[DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero); $input=[IO.File]::OpenRead((Join-Path $env:AURIFY_PAYLOAD_SOURCE $name)); try { $output=$entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() } } } finally { $zip.Dispose() }",
  ],
  {
    cwd: root,
    env: {
      ...buildEnvironment,
      AURIFY_PAYLOAD_SOURCE: packageRoot,
      AURIFY_PAYLOAD_ARCHIVE: payloadArchive,
      AURIFY_PAYLOAD_ENTRIES: JSON.stringify(entries),
    },
    stdio: "pipe",
    windowsHide: true,
  },
);
const compilerArgs = [
  toolchain.compiler,
  "/nologo",
  "/noconfig",
  "/nostdlib+",
  "/deterministic+",
  "/langversion:5",
  `/pathmap:${root}=/_/Aurify`,
  "/target:winexe",
  "/platform:anycpu",
  "/optimize+",
  "/utf8output",
  `/win32icon:${join(root, "companion", "branding", "aurify.ico")}`,
  ...toolchain.references.map((reference) => `/reference:${reference}`),
];
const source = join(root, "companion", "launcher.cs");
const nativeSources = [
  "Contracts.cs",
  "Core.cs",
  "Game.cs",
  "Addons.cs",
  "AddonUpdates.cs",
  "AddonVersions.cs",
  "Update.cs",
  "LuaData.cs",
  "Professions.cs",
  "Overlord.cs",
  "Activity.cs",
  "Characters.cs",
  "Snapshots.cs",
  "CharacterModels.cs",
  "ModelRuntime.cs",
  "Combat.cs",
  "ForeverClient.cs",
  "ForeverSync.cs",
  "ForeverTimeZone.cs",
  "Host.cs",
].map((file) => join(root, "companion", "native", file));
// A thin, non-installing GUI worker allows removal of the mapped installed EXE.
execFileSync(
  toolchain.host,
  [
    ...compilerArgs,
    "/define:UNINSTALL_WORKER",
    `/out:${uninstallWorker}`,
    source,
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
execFileSync(
  toolchain.host,
  [
    ...compilerArgs,
    `/win32manifest:${join(root, "companion", "app.manifest")}`,
    `/out:${executable}`,
    `/resource:${payloadArchive},Aurify.Payload.zip`,
    `/resource:${uninstallWorker},Aurify.Uninstall.exe`,
    `/resource:${join(root, "companion", "third-party", "UNICODE-LICENSE")},Aurify.Unicode.License`,
    `/resource:${join(root, "companion", "update-public-key.xml")},Aurify.Update.PublicKey`,
    `/resource:${join(dist, "Aurify-models.json")},Aurify.ModelRuntime`,
    source,
    ...nativeSources,
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
const executableBytes = await readFile(executable);
const hashes = { "Aurify.exe": digest(executableBytes) };
for (const entry of entries)
  hashes[entry] = digest(await readFile(join(packageRoot, entry)));
const releaseId = digest(
  Buffer.from(
    Object.entries(hashes)
      .map(([path, hash]) => `${path}\n${hash}\n`)
      .join(""),
  ),
);
await writeFile(
  join(dist, "Aurify-release.json"),
  JSON.stringify({
    schema: 1,
    product: "Aurify",
    version: "2.0.6",
    releaseId,
    payloadSha256: hashes["Aurify.exe"],
    payloadLength: executableBytes.length,
    hashes,
    toolchain: toolchain.provenance,
  }) + "\n",
);
console.log(`Built standalone ${executable}`);
